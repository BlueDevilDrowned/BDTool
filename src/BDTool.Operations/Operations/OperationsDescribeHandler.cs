using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Operations;

/// <summary>
/// operations.describe：输入严格为 {operationId}。未知 ID 返回 NOT_FOUND，
/// 多余字段或错误类型返回 INVALID_ARGUMENT，不把目录细节泄漏成通用对象。
/// </summary>
public sealed class OperationsDescribeHandler : IOperationHandler
{
    public const string StableOperationId = "operations.describe";

    private readonly IOperationCatalog _catalog;

    public OperationsDescribeHandler(IOperationCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string OperationId => StableOperationId;

    public Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string operationId;
        try
        {
            using var input = OperationInputParser.ParseObject(request.InputJson, "operationId");
            operationId = OperationInputParser.RequireString(input.RootElement, "operationId");
        }
        catch (OperationInputException exception)
        {
            return Task.FromResult<OperationResult>(OperationResults.Failure<OperationsDescribeData>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message));
        }

        var descriptor = _catalog.Find(operationId);
        if (descriptor is null)
        {
            return Task.FromResult<OperationResult>(OperationResults.Failure<OperationsDescribeData>(
                ErrorCodes.NotFound,
                "catalog",
                "操作目录中不存在：" + operationId + "。",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = operationId
                }));
        }

        return Task.FromResult<OperationResult>(new OperationResult<OperationsDescribeData>
        {
            Status = OperationStatus.Completed,
            Data = new OperationsDescribeData
            {
                Operation = OperationViewMapper.ToDetail(descriptor)
            }
        });
    }
}
