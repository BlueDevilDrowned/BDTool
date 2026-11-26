using System;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Providers;
using BDTool.Provider.Local;

namespace BDTool.Tests.Workspace;

/// <summary>Local Provider 的公开描述必须与 M1 冻结契约一致。</summary>
public sealed class LocalWorkspaceDescriptorTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task DescribeAsync_ReturnsFrozenWorkspaceDescriptor()
    {
        var kernel = _project.CreateKernel();

        var descriptor = await kernel.DescribeAsync(CancellationToken.None);

        Assert.Equal("local", descriptor.Id);
        Assert.Equal("Local Workspace", descriptor.DisplayName);
        Assert.Equal(ProviderKinds.Workspace, descriptor.Kind);
        Assert.Equal(BDToolContract.Version, descriptor.ContractVersion);
        Assert.Equal("0.1.0", descriptor.ImplementationVersion);
        Assert.Equal(string.Empty, descriptor.UnityRange);
        Assert.Equal(4, descriptor.Capabilities.Count);
        Assert.True(descriptor.Capabilities[CapabilityIds.WorkspaceRead]);
        Assert.True(descriptor.Capabilities[CapabilityIds.WorkspaceWrite]);
        Assert.True(descriptor.Capabilities[CapabilityIds.WorkspaceSearch]);
        Assert.True(descriptor.Capabilities[CapabilityIds.WorkspaceFingerprint]);
    }

    [Fact]
    public async Task DescribeAsync_ExposesDefaultLimits()
    {
        var descriptor = await _project.CreateKernel().DescribeAsync(CancellationToken.None);

        Assert.Equal("1048576", descriptor.Limits["maxReadBytes"]);
        Assert.Equal("1048576", descriptor.Limits["maxSearchFileBytes"]);
        Assert.Equal("200", descriptor.Limits["maxSearchResults"]);
        Assert.Equal("1048576", descriptor.Limits["maxApplyBytes"]);
    }

    [Fact]
    public async Task DescribeAsync_UsesConfiguredLimitValues()
    {
        var kernel = _project.CreateKernel(options =>
        {
            options.MaxReadBytes = 16;
            options.MaxSearchFileBytes = 32;
            options.MaxSearchResults = 8;
            options.MaxApplyBytes = 64;
        });

        var descriptor = await kernel.DescribeAsync(CancellationToken.None);

        Assert.Equal("16", descriptor.Limits["maxReadBytes"]);
        Assert.Equal("32", descriptor.Limits["maxSearchFileBytes"]);
        Assert.Equal("8", descriptor.Limits["maxSearchResults"]);
        Assert.Equal("64", descriptor.Limits["maxApplyBytes"]);
    }

    [Fact]
    public async Task DescribeAsync_HonorsCancellation()
    {
        var kernel = _project.CreateKernel();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            kernel.DescribeAsync(source.Token));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _project.CreateKernel(options => options.MaxReadBytes = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _project.CreateKernel(options => options.MaxSearchFileBytes = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _project.CreateKernel(options => options.MaxSearchResults = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _project.CreateKernel(options => options.MaxApplyBytes = 0));
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
