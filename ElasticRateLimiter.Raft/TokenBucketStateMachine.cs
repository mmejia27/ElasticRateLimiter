using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using System;
using System.Collections.Generic;
using System.Text;

namespace ElasticRateLimiter.Raft
{
    public class TokenBucketStateMachine : SimpleStateMachine
    {
        public TokenBucketStateMachine(DirectoryInfo location) : base(location)
        {
        }

        protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        protected override ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
        {
            throw new NotImplementedException();
        }
    }
}
