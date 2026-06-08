using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELPLANT.DiagnosticLogger.Models.Config
{
    public class PlcConfig
    {
        public string Name { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        public string AmsNetId { get; set; } = string.Empty;

        public int Port { get; set; }

        public int RetentionDays { get; set; }

        public int ConnectTimeoutMs { get; set; } = 5000;

        public int ReadTimeoutMs { get; set; } = 2000;

        public int ReconnectIntervalSeconds { get; set; } = 10;

        public List<ParameterConfig> Parameters { get; set; } = [];
    }
}
