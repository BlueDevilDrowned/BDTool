namespace BDTool.Contracts.Errors
{
    /// <summary>
    /// 稳定公共错误码。Provider 可以附加内部诊断字段，
    /// 但不得增加只有单个 Provider 理解的公共错误码。
    /// </summary>
    public static class ErrorCodes
    {
        public const string InvalidArgument = "INVALID_ARGUMENT";
        public const string NotFound = "NOT_FOUND";
        public const string ProviderNotFound = "PROVIDER_NOT_FOUND";
        public const string ProviderVersionMismatch = "PROVIDER_VERSION_MISMATCH";
        public const string CapabilityUnsupported = "CAPABILITY_UNSUPPORTED";
        public const string EnvironmentUnsupported = "ENVIRONMENT_UNSUPPORTED";
        public const string ProjectNotFound = "PROJECT_NOT_FOUND";
        public const string UnityVersionUnsupported = "UNITY_VERSION_UNSUPPORTED";
        public const string EditorNotFound = "EDITOR_NOT_FOUND";
        public const string EditorNotReady = "EDITOR_NOT_READY";
        public const string EditorDisconnected = "EDITOR_DISCONNECTED";
        public const string AssetBusy = "ASSET_BUSY";
        public const string Timeout = "TIMEOUT";
        public const string Cancelled = "CANCELLED";
        public const string ExecutionFailed = "EXECUTION_FAILED";
        public const string ExecutionUncertain = "EXECUTION_UNCERTAIN";
        public const string InternalError = "INTERNAL_ERROR";
    }
}
