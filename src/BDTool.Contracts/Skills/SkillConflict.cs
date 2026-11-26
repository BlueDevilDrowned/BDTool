namespace BDTool.Contracts.Skills
{
    /// <summary>
    /// 阻止 Skill 安装的目标冲突。冲突在复制任何文件之前收集，
    /// 调用方可以据此提示用户处理已有目录或非法标记。
    /// </summary>
    public sealed class SkillConflict
    {
        public string Name { get; set; } = string.Empty;

        public string DestinationPath { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }
}
