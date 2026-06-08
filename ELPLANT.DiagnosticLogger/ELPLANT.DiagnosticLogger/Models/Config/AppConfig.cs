using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELPLANT.DiagnosticLogger.Models.Config
{
    public class AppConfig
    {
        public string ApplicationName { get; set; } = string.Empty;

        public string SystemName { get; set; } = string.Empty;

        public StorageConfig Storage { get; set; } = new();

        public List<PlcConfig> Plcs { get; set; } = [];
    }
}
