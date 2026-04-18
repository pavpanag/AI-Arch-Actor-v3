using System;

namespace AaltoSystemV3
{
    /// <summary>
    /// One replayable scenic decision for a single turn.
    /// Serialized as one JSON line in ScenicEventLogger.
    /// </summary>
    [Serializable]
    public class ScenicEventLogEntry
    {
        public string sessionId;
        public int turnIndex;
        public string timestampUtc;

        public string latestMoveText;
        public string dialogueWindowUsed;
        public string characterSummary;

        public string previousObjective;
        public string previousStance;
        public string updatedObjective;
        public string updatedStance;

        public string intendedAction;
        public string selectedActionLabel;

        public string resolvedMemoryId;
        public string resolvedMemoryLabel;

        public string reason;
        public bool executionSucceeded;
        public string executionResult;

        // Stable API trace identifiers for unambiguous event->trace matching.
        public string traceSessionId;
        public string traceRequestId;
        public string traceContextTag;

        // Optional reference to raw OpenAI response artifact (trace file/path/request id, etc.)
        public string rawResponseReference;
    }
}
