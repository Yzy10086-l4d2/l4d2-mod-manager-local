using System;
using System.Collections.Generic;

namespace Installer
{
    // 阶段 1：识别证据与置信度（只读推导，不改变既有判定）
    public enum ConfTier { High = 3, Medium = 2, Low = 1, Unknown = 0 }

    public enum IdStatus { Identified, Review, Unknown, Mixed }

    public sealed class Evidence
    {
        public string Asset = "";     // 命中的资源路径/名字
        public string RuleId = "";    // 规则标识（对应 Trace）
        public string Hit = "";       // 命中内容
        public string Match = "";     // 匹配方式：exact/segment/prefix/contains/first-hit
        public string Subject = "";   // 该条证据指向的对象
        public ConfTier Tier = ConfTier.Unknown;
    }

    public static class EvidenceUtil
    {
        // 依据强度 → 置信等级（阶段 1 的固定映射，不引入打分）
        public static ConfTier TierOfLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return ConfTier.Unknown;
            if (level == "模型" || level == "地图") return ConfTier.High;
            if (level == "HUD图标" || level == "音效" || level == "平台" || level == "整套") return ConfTier.Medium;
            if (level == "材质" || level == "文件名" || level == "标题") return ConfTier.Low;
            return ConfTier.Unknown;
        }

        public static IdStatus StatusOf(string subject, ConfTier tier, bool multi)
        {
            if (multi) return IdStatus.Mixed;
            if (string.IsNullOrEmpty(subject)) return ConfTier.Unknown == tier ? IdStatus.Unknown : IdStatus.Review;
            if (tier == ConfTier.Low) return IdStatus.Review;
            return IdStatus.Identified;
        }
    }
}
