// Pure feature-gate and migration allowlist policy for Phase 3.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// New playback is opt-in twice: the feature flag must be on and the cue id
    /// must be explicitly allowlisted.  Any missing plan or validation failure
    /// is handled by the caller as a safe fallback to the legacy path.
    /// </summary>
    public sealed class CueTimelineFeatureGate
    {
        private readonly HashSet<string> allowlist = new HashSet<string>(StringComparer.Ordinal);

        public bool Enabled { get; set; }
        public int AllowlistCount { get { return allowlist.Count; } }

        public void SetAllowlist(IEnumerable<string> cueIds)
        {
            allowlist.Clear();
            if (cueIds == null) return;
            foreach (string cueId in cueIds)
            {
                if (!string.IsNullOrEmpty(cueId)) allowlist.Add(cueId);
            }
        }

        public bool IsAllowlisted(string cueId)
        {
            return !string.IsNullOrEmpty(cueId) && allowlist.Contains(cueId);
        }

        public bool ShouldUse(string cueId, out string reason)
        {
            if (!Enabled)
            {
                reason = "feature flag disabled";
                return false;
            }
            if (string.IsNullOrEmpty(cueId))
            {
                reason = "cue id is empty";
                return false;
            }
            if (!allowlist.Contains(cueId))
            {
                reason = "cue id is not allowlisted";
                return false;
            }
            reason = "enabled and allowlisted";
            return true;
        }
    }
}
