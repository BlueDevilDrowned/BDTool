using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Operations;

namespace BDTool.Contracts.Formation
{
    /// <summary>
    /// M7.1 人工编队服务。所有 mutation 通过 requestId 幂等；
    /// 这里不创建 Agent、不绑定 Session，也不改变 Provider 权限。
    /// </summary>
    public interface IFormationService
    {
        Task<OperationResult<FormationCommandData>> CreateFormationAsync(
            CreateFormationRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> AddSeatAsync(
            AddSeatRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> CreateTaskAsync(
            CreateFormationTaskRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> DispatchTaskAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> StartManualTaskAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> SubmitTaskAsync(
            SubmitFormationTaskRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> ReviewTaskAsync(
            ReviewFormationTaskRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> PrepareReworkAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> CancelTaskAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> CancelFormationAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationCommandData>> ArchiveFormationAsync(
            TaskCommandRequest request,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationData>> GetFormationAsync(
            string formationId,
            bool includeReceipts,
            CancellationToken cancellationToken);

        Task<OperationResult<FormationListData>> ListFormationsAsync(
            CancellationToken cancellationToken);
    }
}
