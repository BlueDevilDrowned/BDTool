using System;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Operations;

/// <summary>
/// operations.list：返回当前目录的 typed 摘要列表。输入只允许空对象，
/// 未知字段属于参数错误，便于脚本尽早发现拼写问题。
/// </summary>
public sealed class OperationsListHandler : IOperationHandler
{
    public const string StableOperationId = "operations.list";

    private readonly IOperationCatalog _catalog;

    public OperationsListHandler(IOperationCatalog catalog)
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

        try
        {
            using var input = OperationInputParser.ParseObject(request.InputJson);
            _ = input;

            var data = new OperationsListData();
            foreach (var descriptor in _catalog.GetAll())
            {
                data.Operations.Add(OperationViewMapper.ToSummary(descriptor));
            }

            return Task.FromResult<OperationResult>(new OperationResult<OperationsListData>
            {
                Status = OperationStatus.Completed,
                Data = data
            });
        }
        catch (OperationInputException exception)
        {
            return Task.FromResult<OperationResult>(OperationResults.Failure<OperationsListData>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message));
        }
    }
}
