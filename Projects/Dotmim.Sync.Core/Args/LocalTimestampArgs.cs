﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;

namespace Dotmim.Sync
{
    public class LocalTimestampLoadedArgs : ProgressArgs
    {
        public LocalTimestampLoadedArgs(SyncContext context, long localTimestamp, DbConnection connection, DbTransaction transaction) : base(context, connection, transaction)
        {
            this.LocalTimestamp = localTimestamp;
        }

        public long LocalTimestamp { get; }
    }
    public class LocalTimestampLoadingArgs : ProgressArgs
    {
        public bool Cancel { get; set; } = false;
        public DbCommand Command { get; }

        public LocalTimestampLoadingArgs(SyncContext context, DbCommand command, DbConnection connection, DbTransaction transaction) : base(context, connection, transaction)
        {
            this.Command = command;
        }
    }
}
