using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Contracts.Capabilities
{
    /// <summary>
    /// 当前组合根实际可用能力的快照。UI 和用例必须据此隐藏或拒绝不支持的功能。
    /// </summary>
    public sealed class CapabilitySnapshot
    {
        public string ProviderId { get; set; } = string.Empty;
        public string ContractVersion { get; set; } = string.Empty;
        public Dictionary<string, bool> Capabilities { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, string> Limits { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>能力检查端口。未知能力必须返回不支持，而不是猜测。</summary>
    public interface ICapabilityInspector
    {
        Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken);
    }
}
