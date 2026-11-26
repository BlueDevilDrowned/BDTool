using System.Collections.Generic;

namespace BDTool.Contracts.Skills
{
    /// <summary>
    /// Skill 安装的完整结果。存在 Conflicts 时 Skills 为空，
    /// 表示安装器没有写入任何一个目标目录。
    /// </summary>
    public sealed class SkillInstallResult
    {
        public string Scope { get; set; } = string.Empty;

        public string ProjectPath { get; set; } = string.Empty;

        public string SkillsRoot { get; set; } = string.Empty;

        public List<InstalledSkill> Skills { get; set; } = new List<InstalledSkill>();

        public List<SkillConflict> Conflicts { get; set; } = new List<SkillConflict>();
    }
}
