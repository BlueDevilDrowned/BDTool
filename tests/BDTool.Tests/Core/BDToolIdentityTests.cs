using BDTool.Core;
using BDTool.Operations.Diagnostics;

namespace BDTool.Tests.Core;

/// <summary>
/// 产品身份与里程碑的单一来源约束：CLI --version、Doctor 报告与 Core 常量
/// 必须一致；M1 只提升里程碑，产品版本仍保留 0.1.0。
/// </summary>
public sealed class BDToolIdentityTests
{
    [Fact]
    public void Milestone_IsM1_AndProductVersionRemainsZeroOneZero()
    {
        Assert.Equal("M1", BDToolIdentity.Milestone);
        Assert.Equal("0.1.0", BDToolIdentity.Version);
        Assert.Equal(BDToolIdentity.Milestone, DoctorHandler.CurrentMilestone);
    }
}
