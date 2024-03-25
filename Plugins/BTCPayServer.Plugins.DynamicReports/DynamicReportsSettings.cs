using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.DynamicReports;

public class DynamicReportsSettings
{
    public Dictionary<string, DynamicReportSetting> Reports { get; set; } = new();

    public class DynamicReportSetting
    {
        [Required]
        public string Sql { get; set; }
        
        public bool AllowForNonAdmins { get; set; }
    }
}

public class DynamicReportViewModel:DynamicReportsSettings.DynamicReportSetting
{
    [Required]
    public string Name { get; set; }
    
}
