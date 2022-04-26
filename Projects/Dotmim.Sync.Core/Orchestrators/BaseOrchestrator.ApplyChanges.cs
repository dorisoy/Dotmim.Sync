﻿using Dotmim.Sync.Args;
using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class BaseOrchestrator
    {
        /// <summary>
        /// Apply changes : Delete / Insert / Update
        /// the fromScope is local client scope when this method is called from server
        /// the fromScope is server scope when this method is called from client
        /// </summary>
        internal virtual async Task<(SyncContext, DatabaseChangesApplied)>
            InternalApplyChangesAsync(IScopeInfo scopeInfo, MessageApplyChanges message, DbConnection connection, DbTransaction transaction,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            // call interceptor
            var context = this.GetContext(scopeInfo.Name);
            var databaseChangesApplyingArgs = new DatabaseChangesApplyingArgs(context, message, connection, transaction);
            await this.InterceptAsync(databaseChangesApplyingArgs, progress, cancellationToken).ConfigureAwait(false);

            var changesApplied = new DatabaseChangesApplied();

            // Check if we have some data available
            var hasChanges = message.BatchInfo.HasData();

            var threadNumberLimits = this.Provider.SupportsMultipleActiveResultSets ? 16 : 1;

            // if we have changes or if we are in re init mode
            if (hasChanges || context.SyncType != SyncType.Normal)
            {
                var schemaTables = message.Schema.Tables.SortByDependencies(tab => tab.GetRelations().Select(r => r.GetParentTable()));

                // contains list of table that have been done
                var doneTables = new List<SyncTable>();

                // Disable check constraints
                // Because Sqlite does not support "PRAGMA foreign_keys=OFF" Inside a transaction
                // Report this disabling constraints brefore opening a transaction
                if (message.DisableConstraintsOnApplyChanges)
                {
                    foreach (var table in schemaTables)
                    {
                        await this.InternalDisableConstraintsAsync(scopeInfo, this.GetSyncAdapter(table, scopeInfo), connection, transaction).ConfigureAwait(false);
                    }
                }

                // -----------------------------------------------------
                // 0) Check if we are in a reinit mode (Check also SyncWay to be sure we don't reset tables on server, then check if we don't have already applied a snapshot)
                // -----------------------------------------------------
                if (context.SyncWay == SyncWay.Download && context.SyncType != SyncType.Normal && !message.SnapshoteApplied)
                {
                    foreach (var table in schemaTables.Reverse())
                    {
                        await this.InternalResetTableAsync(scopeInfo, this.GetSyncAdapter(table, scopeInfo), connection, transaction).ConfigureAwait(false);
                    }
                }

                // Trying to change order (from deletes-upserts to upserts-deletes)
                // see https://github.com/Mimetis/Dotmim.Sync/discussions/453#discussioncomment-380530

                // -----------------------------------------------------
                // 1) Applying Inserts and Updates. Apply in table order
                // -----------------------------------------------------
                if (hasChanges)
                {
                    foreach (var table in schemaTables)
                    {
                        await this.InternalApplyTableChangesAsync(scopeInfo, table, message, connection, transaction,
                            DataRowState.Modified, changesApplied, cancellationToken, progress).ConfigureAwait(false);
                    }
                }

                // -----------------------------------------------------
                // 2) Applying Deletes. Do not apply deletes if we are in a new database
                // -----------------------------------------------------
                if (!message.IsNew && hasChanges)
                {
                    foreach (var table in schemaTables.Reverse())
                    {
                        await this.InternalApplyTableChangesAsync(scopeInfo, table, message, connection, transaction,
                            DataRowState.Deleted, changesApplied, cancellationToken, progress).ConfigureAwait(false);
                    }
                }

                // Re enable check constraints
                if (message.DisableConstraintsOnApplyChanges)
                    foreach (var table in schemaTables)
                        await this.InternalEnableConstraintsAsync(scopeInfo, this.GetSyncAdapter(table, scopeInfo), connection, transaction).ConfigureAwait(false);

                // Dispose data
                message.BatchInfo.Clear(false);
            }

            // Before cleaning, check if we are not applying changes from a snapshotdirectory
            var cleanFolder = message.CleanFolder;

            if (cleanFolder)
                cleanFolder = await this.InternalCanCleanFolderAsync(context, message.BatchInfo, cancellationToken, progress).ConfigureAwait(false);

            // clear the changes because we don't need them anymore
            message.BatchInfo.Clear(cleanFolder);

            var databaseChangesAppliedArgs = new DatabaseChangesAppliedArgs(context, changesApplied, connection, transaction);
            await this.InterceptAsync(databaseChangesAppliedArgs, progress, cancellationToken).ConfigureAwait(false);

            return (context, changesApplied);

        }

        /// <summary>
        /// Apply changes internal method for one type of query: Insert, Update or Delete for every batch from a table
        /// </summary>
        private async Task InternalApplyTableChangesAsync(IScopeInfo scopeInfo, SyncTable schemaTable, MessageApplyChanges message,
            DbConnection connection, DbTransaction transaction, DataRowState applyType, DatabaseChangesApplied changesApplied,
            CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            // Only table schema is replicated, no datas are applied
            if (schemaTable.SyncDirection == SyncDirection.None)
                return;

            var context = this.GetContext(scopeInfo.Name);

            // if we are in upload stage, so check if table is not download only
            if (context.SyncWay == SyncWay.Upload && schemaTable.SyncDirection == SyncDirection.DownloadOnly)
                return;

            // if we are in download stage, so check if table is not download only
            if (context.SyncWay == SyncWay.Download && schemaTable.SyncDirection == SyncDirection.UploadOnly)
                return;

            var hasChanges = message.BatchInfo.HasData(schemaTable.TableName, schemaTable.SchemaName);

            // Each table in the messages contains scope columns. Don't forget it
            if (!hasChanges)
                return;

            // what kind of command to execute
            var init = message.IsNew || context.SyncType != SyncType.Normal;
            DbCommandType dbCommandType = applyType == DataRowState.Deleted ? DbCommandType.DeleteRows : (init ? DbCommandType.InsertRows : DbCommandType.UpdateRows);

            // tmp sync table with only writable columns
            var changesSet = schemaTable.Schema.Clone(false);
            var schemaChangesTable = DbSyncAdapter.CreateChangesTable(schemaTable, changesSet);

            // get executioning adapter
            var syncAdapter = this.GetSyncAdapter(schemaChangesTable, scopeInfo);
            syncAdapter.ApplyType = applyType;

            // Get command
            var (command, isBatch) = await syncAdapter.GetCommandAsync(dbCommandType, connection, transaction);

            if (command == null) return;

            var bpiTables = message.BatchInfo.GetBatchPartsInfo(schemaTable);

            // launch interceptor if any
            var args = new TableChangesApplyingArgs(context, message.BatchInfo, bpiTables, schemaTable, applyType, command, connection, transaction);
            await this.InterceptAsync(args, progress, cancellationToken).ConfigureAwait(false);

            if (args.Cancel || args.Command == null)
                return;

            command = args.Command;
            var cmdText = command.CommandText;

            TableChangesApplied tableChangesApplied = null;

            // Conflicts occured when trying to apply rows
            var conflictRows = new List<SyncRow>();

            // I've got all files for my table
            // applied rows for this bpi
            foreach (var batchPartInfo in bpiTables)
            {
                // Applied row for this particular BPI
                var appliedRowsTmp = 0;
                // Rows fetch (either of the good state or not) from the BPI
                var rowsFetched = 0;

                // Get full path of my batchpartinfo
                var fullPath = message.BatchInfo.GetBatchPartInfoPath(batchPartInfo).FullPath;

                // accumulating rows
                var batchRows = new List<SyncRow>();

                var localSerializer = message.LocalSerializerFactory.GetLocalSerializer();
                if (isBatch)
                {
                    foreach (var syncRow in localSerializer.ReadRowsFromFile(fullPath, schemaChangesTable))
                    {
                        rowsFetched++;

                        // Adding rows to the batch rows
                        if (batchRows.Count < this.Provider.BulkBatchMaxLinesCount)
                        {
                            if (syncRow.RowState == applyType)
                                batchRows.Add(syncRow);

                            if (rowsFetched < batchPartInfo.RowsCount && batchRows.Count < this.Provider.BulkBatchMaxLinesCount)
                                continue;
                        }
                        var failedRows = schemaChangesTable.Schema.Clone().Tables[schemaChangesTable.TableName, schemaChangesTable.SchemaName];

                        command.CommandText = cmdText;
                        var batchArgs = new TableChangesApplyingSyncRowsArgs(context, message.BatchInfo, batchRows, schemaChangesTable, applyType, command, connection, transaction);
                        await this.InterceptAsync(batchArgs, progress, cancellationToken).ConfigureAwait(false);

                        if (batchArgs.Cancel || batchArgs.Command == null)
                            continue;

                        // get the correct pointer to the command from the interceptor in case user change the whole instance
                        command = batchArgs.Command;

                        await this.InterceptAsync(new DbCommandArgs(context, command, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                        // execute the batch, through the provider
                        await syncAdapter.ExecuteBatchCommandAsync(command, message.SenderScopeId, batchArgs.SyncRows, schemaChangesTable, failedRows, message.LastTimestamp, connection, transaction).ConfigureAwait(false);

                        foreach (var failedRow in failedRows.Rows)
                            conflictRows.Add(failedRow);

                        //rows minus failed rows
                        appliedRowsTmp += batchRows.Count - failedRows.Rows.Count;
                        batchRows.Clear();
                    }
                }
                else
                {
                    foreach (var syncRow in localSerializer.ReadRowsFromFile(fullPath, schemaChangesTable))
                    {
                        rowsFetched++;

                        if (syncRow.RowState != applyType)
                            continue;

                        command.CommandText = cmdText;
                        var batchArgs = new TableChangesApplyingSyncRowsArgs(context, message.BatchInfo, new List<SyncRow> { syncRow }, schemaChangesTable, applyType, command, connection, transaction);
                        await this.InterceptAsync(batchArgs, progress, cancellationToken).ConfigureAwait(false);

                        if (batchArgs.Cancel || batchArgs.Command == null || batchArgs.SyncRows == null || batchArgs.SyncRows.Count() <= 0)
                            continue;

                        // get the correct pointer to the command from the interceptor in case user change the whole instance
                        command = batchArgs.Command;

                        // Set the parameters value from row 
                        syncAdapter.SetColumnParametersValues(command, batchArgs.SyncRows.First());

                        // Set the special parameters for update
                        syncAdapter.AddScopeParametersValues(command, message.SenderScopeId, message.LastTimestamp, applyType == DataRowState.Deleted, false);

                        await this.InterceptAsync(new DbCommandArgs(context, command, connection, transaction), progress, cancellationToken).ConfigureAwait(false);

                        var rowAppliedCount = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                        // Check if we have a return value instead
                        var syncRowCountParam = DbSyncAdapter.GetParameter(command, "sync_row_count");

                        if (syncRowCountParam != null)
                            rowAppliedCount = (int)syncRowCountParam.Value;

                        if (rowAppliedCount > 0)
                            appliedRowsTmp++;
                        else
                            conflictRows.Add(syncRow);


                    }

                }


                // conflict rows applied
                int rowsAppliedCount = 0;
                // conflict resolved count
                int conflictsResolvedCount = 0;

                // If conflicts occured
                if (conflictRows.Count > 0)
                {

                    foreach (var conflictRow in conflictRows)
                    {
                        var (conflictResolvedCount, resolvedRow, rowAppliedCount) =
                            await this.HandleConflictAsync(scopeInfo, message.LocalScopeId, message.SenderScopeId, syncAdapter, context, conflictRow, schemaChangesTable,
                                                           message.Policy, message.LastTimestamp, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                        conflictsResolvedCount += conflictResolvedCount;
                        rowsAppliedCount += rowAppliedCount;

                    }

                    // add rows with resolved conflicts
                    appliedRowsTmp += rowsAppliedCount;
                }

                // Any failure ?
                var changedFailed = rowsFetched - conflictsResolvedCount - appliedRowsTmp;

                // Only Upsert DatabaseChangesApplied if we make an upsert/ delete from the batch or resolved any conflict
                if (appliedRowsTmp > 0 || conflictsResolvedCount > 0)
                {
                    // We may have multiple batch files, so we can have multipe sync tables with the same name
                    // We can say that a syncTable may be contained in several files
                    // That's why we should get an applied changes instance if already exists from a previous batch file
                    tableChangesApplied = changesApplied.TableChangesApplied.FirstOrDefault(tca =>
                    {
                        var sc = SyncGlobalization.DataSourceStringComparison;

                        var sn = tca.SchemaName == null ? string.Empty : tca.SchemaName;
                        var otherSn = schemaTable.SchemaName == null ? string.Empty : schemaTable.SchemaName;

                        return tca.TableName.Equals(schemaTable.TableName, sc) &&
                               sn.Equals(otherSn, sc) &&
                               tca.State == applyType;
                    });

                    if (tableChangesApplied == null)
                    {
                        tableChangesApplied = new TableChangesApplied
                        {
                            TableName = schemaTable.TableName,
                            SchemaName = schemaTable.SchemaName,
                            Applied = appliedRowsTmp,
                            ResolvedConflicts = conflictsResolvedCount,
                            Failed = changedFailed,
                            State = applyType,
                            TotalRowsCount = message.BatchInfo.RowsCount,
                            TotalAppliedCount = changesApplied.TotalAppliedChanges + appliedRowsTmp
                        };
                        changesApplied.TableChangesApplied.Add(tableChangesApplied);
                    }
                    else
                    {
                        tableChangesApplied.Applied += appliedRowsTmp;
                        tableChangesApplied.TotalAppliedCount = changesApplied.TotalAppliedChanges;
                        tableChangesApplied.ResolvedConflicts += conflictsResolvedCount;
                        tableChangesApplied.Failed += changedFailed;
                    }

                    // we've got 0.25% to fill here 
                    var progresspct = appliedRowsTmp * 0.25d / tableChangesApplied.TotalRowsCount;
                    context.ProgressPercentage += progresspct;

                    // Report the batch changes applied
                    var tableChangesBatchAppliedArgs = new TableChangesBatchAppliedArgs(context, tableChangesApplied, connection, transaction);
                    await this.InterceptAsync(tableChangesBatchAppliedArgs, progress, cancellationToken).ConfigureAwait(false);
                }

            }

            schemaChangesTable.Dispose();
            schemaChangesTable = null;
            changesSet.Dispose();
            changesSet = null;

            // Report the overall changes applied for the current table
            if (tableChangesApplied != null)
            {
                var tableChangesAppliedArgs = new TableChangesAppliedArgs(context, tableChangesApplied, connection, transaction);
                // We don't report progress if we do not have applied any changes on the table, to limit verbosity of Progress
                await this.InterceptAsync(tableChangesAppliedArgs, progress, cancellationToken).ConfigureAwait(false);
            }

        }

        /// <summary>
        /// Handle a conflict
        /// The int returned is the conflict count I need 
        /// </summary>
        private async Task<(int conflictResolvedCount, SyncRow resolvedRow, int rowAppliedCount)> HandleConflictAsync(IScopeInfo scopeInfo,
                                Guid localScopeId, Guid senderScopeId, DbSyncAdapter syncAdapter, SyncContext context, SyncRow conflictRow, SyncTable schemaChangesTable,
                                ConflictResolutionPolicy policy, long? lastTimestamp, DbConnection connection, DbTransaction transaction,
                                CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            SyncRow finalRow;
            SyncRow localRow;
            ConflictType conflictType;
            ApplyAction conflictApplyAction;
            int rowAppliedCount = 0;
            Guid? nullableSenderScopeId;

            (conflictApplyAction, conflictType, localRow, finalRow, nullableSenderScopeId) =
                await this.GetConflictActionAsync(scopeInfo, localScopeId, syncAdapter, conflictRow, schemaChangesTable,
                policy, senderScopeId, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            // Conflict rollbacked by user
            if (conflictApplyAction == ApplyAction.Rollback)
                throw new RollbackException("Rollback action taken on conflict");

            // Local provider wins, update metadata
            if (conflictApplyAction == ApplyAction.Continue)
            {
                var isMergeAction = finalRow != null;
                var row = isMergeAction ? finalRow : localRow;

                // Conflict on a line that is not present on the datasource
                if (row == null)
                    return (conflictResolvedCount: 1, finalRow, rowAppliedCount: 0);

                // if we have a merge action, we apply the row on the server
                if (isMergeAction)
                {
                    // if merge, we update locally the row and let the sync_update_scope_id set to null
                    var isUpdated = await this.InternalApplyConflictUpdateAsync(scopeInfo, syncAdapter, row, lastTimestamp, null, true, connection, transaction).ConfigureAwait(false);
                    // We don't update metadatas so the row is updated (on server side) 
                    // and is mark as updated locally.
                    // and will be returned back to sender, since it's a merge, and we need it on the client

                    if (!isUpdated)
                        throw new Exception("Can't update the merge row.");
                }

                finalRow = isMergeAction ? row : localRow;

                // We don't do anything, since we let the original row. so we resolved one conflict but applied no rows
                return (conflictResolvedCount: 1, finalRow, rowAppliedCount: 0);

            }

            // We gonna apply with force the line
            if (conflictApplyAction == ApplyAction.RetryWithForceWrite)
            {
                // TODO : Should Raise an error ?
                if (conflictRow == null)
                    return (0, finalRow, 0);

                bool operationComplete = false;

                switch (conflictType)
                {
                    // Remote source has row, Local don't have the row, so insert it
                    case ConflictType.RemoteExistsLocalExists:
                        operationComplete = await this.InternalApplyConflictUpdateAsync(scopeInfo, syncAdapter, conflictRow, lastTimestamp, nullableSenderScopeId, true, connection, transaction).ConfigureAwait(false);
                        rowAppliedCount = 1;
                        break;

                    case ConflictType.RemoteExistsLocalNotExists:
                    case ConflictType.RemoteExistsLocalIsDeleted:
                    case ConflictType.UniqueKeyConstraint:
                        operationComplete = await this.InternalApplyConflictUpdateAsync(scopeInfo, syncAdapter, conflictRow, lastTimestamp, nullableSenderScopeId, true, connection, transaction).ConfigureAwait(false);
                        rowAppliedCount = 1;
                        break;

                    // Conflict, but both have delete the row, so just update the metadata to the right winner
                    case ConflictType.RemoteIsDeletedLocalIsDeleted:
                        operationComplete = await this.InternalUpdateMetadatasAsync(scopeInfo, syncAdapter, conflictRow, nullableSenderScopeId, true, connection, transaction).ConfigureAwait(false);
                        rowAppliedCount = 0;
                        break;

                    // The row does not exists locally, and since it's coming from a deleted state, we can forget it
                    case ConflictType.RemoteIsDeletedLocalNotExists:
                        operationComplete = true;
                        rowAppliedCount = 0;
                        break;

                    // The remote has delete the row, and local has insert or update it
                    // So delete the local row
                    case ConflictType.RemoteIsDeletedLocalExists:
                        operationComplete = await this.InternalApplyConflictDeleteAsync(scopeInfo, syncAdapter, conflictRow, lastTimestamp, nullableSenderScopeId, true, connection, transaction);

                        // Conflict, but both have delete the row, so just update the metadata to the right winner
                        if (!operationComplete)
                        {
                            operationComplete = await this.InternalUpdateMetadatasAsync(scopeInfo, syncAdapter, conflictRow, nullableSenderScopeId, true, connection, transaction);
                            rowAppliedCount = 0;

                        }
                        else
                        {
                            rowAppliedCount = 1;
                        }

                        break;

                    case ConflictType.ErrorsOccurred:
                        return (0, finalRow, 0);
                }

                finalRow = conflictRow;

                //After a force update, there is a problem, so raise exception
                if (!operationComplete)
                {
                    //throw new UnknownException("Force update should always work.. contact the author :)");
                    this.Logger.LogError("Force update should always work..");
                }

                return (1, finalRow, rowAppliedCount);
            }

            return (0, finalRow, 0);
        }

        /// <summary>
        /// A conflict has occured, we try to ask for the solution to the user
        /// </summary>
        private async Task<(ApplyAction, ConflictType, SyncRow, SyncRow, Guid?)> GetConflictActionAsync(IScopeInfo scopeInfo, Guid localScopeId, DbSyncAdapter syncAdapter, SyncRow conflictRow,
            SyncTable schemaChangesTable, ConflictResolutionPolicy policy, Guid senderScopeId, DbConnection connection, DbTransaction transaction,
            CancellationToken cancellationToken, IProgress<ProgressArgs> progress)
        {

            // default action
            var resolution = policy == ConflictResolutionPolicy.ClientWins ? ConflictResolution.ClientWins : ConflictResolution.ServerWins;

            // if ConflictAction is ServerWins or MergeRow it's Ok to set to Continue
            var action = ApplyAction.Continue;

            // check the interceptor
            var interceptor = this.interceptors.GetInterceptor<ApplyChangesFailedArgs>();

            SyncRow finalRow = null;
            SyncRow localRow = null;
            Guid? finalSenderScopeId = senderScopeId;

            // default conflict type
            ConflictType conflictType = conflictRow.RowState == DataRowState.Deleted ? ConflictType.RemoteIsDeletedLocalExists : ConflictType.RemoteExistsLocalExists;
            
            var context = this.GetContext(scopeInfo.Name);

            // if is not empty, get the conflict and intercept
            // We don't get the conflict on automatic conflict resolution
            // Since it's an automatic resolution, we don't need to get the local conflict row
            // So far we get the conflict only if an interceptor exists
            if (!interceptor.IsEmpty)
            {
                // Get the localRow
                localRow = await this.InternalGetConflictRowAsync(scopeInfo, syncAdapter, localScopeId, conflictRow, schemaChangesTable, connection, transaction).ConfigureAwait(false);
                // Get the conflict
                var conflict = this.GetConflict(conflictRow, localRow);

                // Interceptor
                var arg = new ApplyChangesFailedArgs(context, conflict, resolution, senderScopeId, connection, transaction);
                await this.InterceptAsync(arg, progress, cancellationToken).ConfigureAwait(false);

                resolution = arg.Resolution;
                finalRow = arg.Resolution == ConflictResolution.MergeRow ? arg.FinalRow : null;
                finalSenderScopeId = arg.SenderScopeId;
                conflictType = arg.Conflict.Type;
            }
            else
            {
                // Check logger, because we make some reflection here
                if (this.Logger.IsEnabled(LogLevel.Debug))
                {
                    var args = new { Row = conflictRow, Resolution = resolution, Connection = connection, Transaction = transaction };
                    this.Logger.LogDebug(new EventId(SyncEventsId.ApplyChangesFailed.Id, "ApplyChangesFailed"), args);
                }
            }

            // Change action only if we choose ClientWins or Rollback.
            // for ServerWins or MergeRow, action is Continue
            if (resolution == ConflictResolution.ClientWins)
                action = ApplyAction.RetryWithForceWrite;
            else if (resolution == ConflictResolution.Rollback)
                action = ApplyAction.Rollback;

            // returning the action to take, and actually the finalRow if action is set to Merge
            return (action, conflictType, localRow, finalRow, finalSenderScopeId);
        }

        /// <summary>
        /// We have a conflict, try to get the source row and generate a conflict
        /// </summary>
        private SyncConflict GetConflict(SyncRow remoteConflictRow, SyncRow localConflictRow)
        {

            var dbConflictType = ConflictType.ErrorsOccurred;

            if (remoteConflictRow == null)
                throw new UnknownException("THAT can't happen...");


            // local row is null
            if (localConflictRow == null && remoteConflictRow.RowState == DataRowState.Modified)
                dbConflictType = ConflictType.RemoteExistsLocalNotExists;
            else if (localConflictRow == null && remoteConflictRow.RowState == DataRowState.Deleted)
                dbConflictType = ConflictType.RemoteIsDeletedLocalNotExists;

            //// remote row is null. Can't happen
            //else if (remoteConflictRow == null && localConflictRow.RowState == DataRowState.Modified)
            //    dbConflictType = ConflictType.RemoteNotExistsLocalExists;
            //else if (remoteConflictRow == null && localConflictRow.RowState == DataRowState.Deleted)
            //    dbConflictType = ConflictType.RemoteNotExistsLocalIsDeleted;

            else if (remoteConflictRow.RowState == DataRowState.Deleted && localConflictRow.RowState == DataRowState.Deleted)
                dbConflictType = ConflictType.RemoteIsDeletedLocalIsDeleted;
            else if (remoteConflictRow.RowState == DataRowState.Modified && localConflictRow.RowState == DataRowState.Deleted)
                dbConflictType = ConflictType.RemoteExistsLocalIsDeleted;
            else if (remoteConflictRow.RowState == DataRowState.Deleted && localConflictRow.RowState == DataRowState.Modified)
                dbConflictType = ConflictType.RemoteIsDeletedLocalExists;
            else if (remoteConflictRow.RowState == DataRowState.Modified && localConflictRow.RowState == DataRowState.Modified)
                dbConflictType = ConflictType.RemoteExistsLocalExists;

            // Generate the conflict
            var conflict = new SyncConflict(dbConflictType);
            conflict.AddRemoteRow(remoteConflictRow);

            if (localConflictRow != null)
                conflict.AddLocalRow(localConflictRow);

            return conflict;
        }

        /// <summary>
        /// Try to get a source row
        /// </summary>
        private async Task<SyncRow> InternalGetConflictRowAsync(IScopeInfo scopeInfo, DbSyncAdapter syncAdapter, Guid localScopeId, SyncRow primaryKeyRow, SyncTable schema, DbConnection connection, DbTransaction transaction)
        {
            // Get the row in the local repository
            var (command, _) = await syncAdapter.GetCommandAsync(DbCommandType.SelectRow, connection, transaction);

            if (command == null) return null;

            // set the primary keys columns as parameters
            syncAdapter.SetColumnParametersValues(command, primaryKeyRow);

            // Create a select table based on the schema in parameter + scope columns
            var changesSet = schema.Schema.Clone(false);
            var selectTable = DbSyncAdapter.CreateChangesTable(schema, changesSet);

            var context = this.GetContext(scopeInfo.Name);
            await this.InterceptAsync(new DbCommandArgs(context, command, connection, transaction)).ConfigureAwait(false);

            using var dataReader = await command.ExecuteReaderAsync().ConfigureAwait(false);

            if (!dataReader.Read())
            {
                dataReader.Close();
                return null;
            }

            // Create a new empty row
            var syncRow = selectTable.NewRow();
            for (var i = 0; i < dataReader.FieldCount; i++)
            {
                var columnName = dataReader.GetName(i);

                // if we have the tombstone value, do not add it to the table
                if (columnName == "sync_row_is_tombstone")
                {
                    var isTombstone = Convert.ToInt64(dataReader.GetValue(i)) > 0;
                    syncRow.RowState = isTombstone ? DataRowState.Deleted : DataRowState.Modified;
                    continue;
                }
                if (columnName == "sync_update_scope_id")
                    continue;

                var columnValueObject = dataReader.GetValue(i);
                var columnValue = columnValueObject == DBNull.Value ? null : columnValueObject;
                syncRow[columnName] = columnValue;
            }

            // if syncRow is not a deleted row, we can check for which kind of row it is.
            if (syncRow != null && syncRow.RowState == DataRowState.Unchanged)
                syncRow.RowState = DataRowState.Modified;

            dataReader.Close();

            return syncRow;
        }

        /// <summary>
        /// Apply a delete on a row
        /// </summary>
        private async Task<bool> InternalApplyConflictDeleteAsync(IScopeInfo scopeInfo, DbSyncAdapter syncAdapter, SyncRow row, long? lastTimestamp, Guid? senderScopeId, bool forceWrite, DbConnection connection, DbTransaction transaction)
        {
            if (row.SchemaTable == null)
                throw new ArgumentException("Schema table is not present in the row");

            var (command, _) = await syncAdapter.GetCommandAsync(DbCommandType.DeleteRow, connection, transaction);

            if (command == null) return false;

            // Set the parameters value from row
            syncAdapter.SetColumnParametersValues(command, row);

            // Set the special parameters for update
            syncAdapter.AddScopeParametersValues(command, senderScopeId, lastTimestamp, true, forceWrite);

            var context = this.GetContext(scopeInfo.Name);
            await this.InterceptAsync(new DbCommandArgs(context, command, connection, transaction)).ConfigureAwait(false);

            var rowDeletedCount = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            // Check if we have a return value instead
            var syncRowCountParam = DbSyncAdapter.GetParameter(command, "sync_row_count");

            if (syncRowCountParam != null)
                rowDeletedCount = (int)syncRowCountParam.Value;

            return rowDeletedCount > 0;
        }

        /// <summary>
        /// Apply a single update in the current datasource. if forceWrite, override conflict situation and force the update
        /// </summary>
        private async Task<bool> InternalApplyConflictUpdateAsync(IScopeInfo scopeInfo, DbSyncAdapter syncAdapter, SyncRow row, long? lastTimestamp, Guid? senderScopeId, bool forceWrite, DbConnection connection, DbTransaction transaction)
        {
            if (row.SchemaTable == null)
                throw new ArgumentException("Schema table is not present in the row");

            var (command, _) = await syncAdapter.GetCommandAsync(DbCommandType.UpdateRow, connection, transaction);

            if (command == null) return false;

            // Set the parameters value from row
            syncAdapter.SetColumnParametersValues(command, row);

            // Set the special parameters for update
            syncAdapter.AddScopeParametersValues(command, senderScopeId, lastTimestamp, false, forceWrite);

            var context = this.GetContext(scopeInfo.Name);

            await this.InterceptAsync(new DbCommandArgs(context, command, connection, transaction)).ConfigureAwait(false);

            var rowUpdatedCount = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            // Check if we have a return value instead
            var syncRowCountParam = DbSyncAdapter.GetParameter(command, "sync_row_count");

            if (syncRowCountParam != null)
                rowUpdatedCount = (int)syncRowCountParam.Value;

            return rowUpdatedCount > 0;
        }


    }
}
