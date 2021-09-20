using System.Text.Json.Serialization;

namespace PnP.Framework.Provisioning.Model.SharePoint.ModernExperiences
{
    public class SectionZoneGroupMetadata
    {
        public bool Collapsible { get; set; }

        public bool IsExpanded { get; set; }

        public bool ShowDividerLine { get; set; }

        public string IconAlignment { get; set; }

        public string DisplayName { get; set; }
    }
}
