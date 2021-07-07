using Dotmim.Sync.Builders;
using Dotmim.Sync.Manager;
using System;
using System.Data.Common;
using System.IO;

namespace Dotmim.Sync.Sqlite
{

    public class VistaDBSyncProvider : CoreProvider
    {


        private string filePath;
        private DbMetadata dbMetadata;
        private static String providerType;
        private DbProviderFactory factory;

        public override DbMetadata GetMetadata()
        {
            return null;
            //if (dbMetadata == null)
            //    dbMetadata = new SqliteDbMetadata();

            //return dbMetadata;
        }


        /// <summary>
        /// Sqlite does not support Bulk operations
        /// </summary>
        public override bool SupportBulkOperations => false;

        /// <summary>
        /// SQLIte does not support to be a server side.
        /// Reason 1 : Can't easily insert / update batch with handling conflict
        /// Reason 2 : Can't filter rows (based on filterclause)
        /// </summary>
        public override bool CanBeServerProvider => false;

        public override string GetProviderTypeName() => ProviderType;

        public static string ProviderType
        {
            get
            {
                if (!string.IsNullOrEmpty(providerType))
                    return providerType;

                Type type = typeof(VistaDBSyncProvider);
                providerType = $"{type.Name}, {type}";

                return providerType;
            }

        }
        public VistaDBSyncProvider() : base()
        {
            // Registering the VistaDB 6 Data Provider
            // You need to manually add the VistaDB nuget package to your project
            // Otherwise this one will not work

            if (!DbProviderFactories.TryGetFactory("VistaDB 6 Data Provider", out this.factory))
            {
                DbProviderFactories.RegisterFactory("VistaDB 6 Data Provider",
                    "VistaDB.Provider.VistaDBProviderFactory, VistaDB.6");
                
                this.factory = DbProviderFactories.GetFactory("VistaDB 6 Data Provider");
            }

        }

        public VistaDBSyncProvider(string filePath) : this()
        {
            var fileInfo = new FileInfo(filePath);

            if (!Directory.Exists(fileInfo.Directory.FullName))
                throw new Exception($"filePath directory {fileInfo.Directory.FullName} does not exists.");

            if (string.IsNullOrEmpty(fileInfo.Name))
                throw new Exception($"Sqlite database file path needs a file name");

            if (!File.Exists(filePath))
            {
                using (var connection = factory.CreateConnection())
                {
                    using var command = connection.CreateCommand();
                    
                    command.Connection = connection;
                    command.CommandText = "CREATE DATABASE 'NEWDB2.vdb6', PAGE SIZE 4, LCID 1033, CASE SENSITIVE FALSE;";
                    command.ExecuteNonQuery();
                }
            }

            //builder.DataSource = filePath;

            //this.ConnectionString = builder.ConnectionString;


            //this.filePath = filePath;



            //var connectionString = @"Data Source='C:\PROJECTS\SAMPLES\VistaDBSample01\NEWDB.vdb6';Pooling=true;";

            //var builder = new SqliteConnectionStringBuilder();

            //if (filePath.ToLowerInvariant().StartsWith("data source"))
            //{
            //    this.ConnectionString = filePath;
            //}
            //else
            //{
            //    var fileInfo = new FileInfo(filePath);

            //    if (!Directory.Exists(fileInfo.Directory.FullName))
            //        throw new Exception($"filePath directory {fileInfo.Directory.FullName} does not exists.");

            //    if (string.IsNullOrEmpty(fileInfo.Name))
            //        throw new Exception($"Sqlite database file path needs a file name");

            //    builder.DataSource = filePath;

            //    this.ConnectionString = builder.ConnectionString;
            //}

        }

        public VistaDBSyncProvider(FileInfo fileInfo) : this()
        {
            this.filePath = fileInfo.FullName;
          
        }


        public VistaDBSyncProvider(DbConnectionStringBuilder vistaDBConnectionStringBuilder) : this()
        {
        }

        public override void EnsureSyncException(SyncException syncException)
        {
            return;
        }

        public override DbConnection CreateConnection()
        {
            return null;

            //var connection = factory.CreateConnection();
            //connection.ConnectionString = this.ConnectionString;
            //return connection;
        }

        public override DbScopeBuilder GetScopeBuilder(string scopeInfoTableName) => null;

        public override DbTableBuilder GetTableBuilder(SyncTable tableDescription, SyncSetup setup)
        {
            return null;
        }
        public override DbBuilder GetDatabaseBuilder() => null;
        public override DbSyncAdapter GetSyncAdapter(SyncTable tableDescription, SyncSetup setup)
        {
            return null;
        }

        public override (ParserName tableName, ParserName trackingName) GetParsers(SyncTable tableDescription, SyncSetup setup)
        {
            string tableAndPrefixName = tableDescription.TableName;
            var originalTableName = ParserName.Parse(tableDescription);

            var pref = setup.TrackingTablesPrefix != null ? setup.TrackingTablesPrefix : "";
            var suf = setup.TrackingTablesSuffix != null ? setup.TrackingTablesSuffix : "";

            // be sure, at least, we have a suffix if we have empty values. 
            // othewise, we have the same name for both table and tracking table
            if (string.IsNullOrEmpty(pref) && string.IsNullOrEmpty(suf))
                suf = "_tracking";

            var trackingTableName = ParserName.Parse($"{pref}{tableAndPrefixName}{suf}");

            return (originalTableName, trackingTableName);
        }
    }
}
