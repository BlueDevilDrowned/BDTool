namespace BDTool.Contracts.Skills
{
    /// <summary>
    /// 单个 Skill 的安装结果快照。SourcePath 与 DestinationPath 均为规范化绝对路径；
    /// Updated 表示本次安装刷新了受管目录，而不是比较内容后得出的“确有变化”。
    /// </summary>
    public sealed class InstalledSkill
    {
        public string Name { get; set; } = string.Empty;

        public string DestinationPath { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public bool Updated { get; set; }
    }
}
