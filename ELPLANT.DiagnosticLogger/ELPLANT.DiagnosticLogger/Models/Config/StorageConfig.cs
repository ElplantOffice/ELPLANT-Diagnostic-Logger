using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELPLANT.DiagnosticLogger.Models.Config
{
    public class StorageConfig
    {
        public string DatasetFolder { get; set; } = string.Empty;

        public string ApplicationLogFolder { get; set; } = string.Empty;

        public int WriteIntervalSeconds { get; set; }

        public int ApplicationLogRetentionDays { get; set; }
    }
}
