using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELPLANT.DiagnosticLogger.Models.Config
{
    public class ParameterConfig
    {
        public string Name { get; set; } = string.Empty;

        public string VarAddress { get; set; } = string.Empty;

        public ParameterReadMode ReadMode { get; set; }

        public string? VarType { get; set; }

        public int? ReadIntervalMs { get; set; }
    }
}
