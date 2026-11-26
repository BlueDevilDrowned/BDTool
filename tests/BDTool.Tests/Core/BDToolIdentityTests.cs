using BDTool.Core;
using BDTool.Operations.Diagnostics;

namespace BDTool.Tests.Core;

/// <summary>
/// 产品身份与里程碑的单一来源约束：CLI --version、Doctor 报告与 Core 常量
/// 必须一致；M7.1 完成时产品版本与里程碑同步提升。
/// </summary>
public sealed class BDToolIdentityTests
{
    [Fact]
    public void Milestone_IsM7Point1_AndProductVersionIsOneTwoZero()
    {
        Assert.Equal("M7.1", BDToolIdentity.Milestone);
        Assert.Equal("1.2.0", BDToolIdentity.Version);
        Assert.Equal(BDToolIdentity.Milestone, DoctorHandler.CurrentMilestone);
    }
}
