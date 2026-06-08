using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ELPLANT.DiagnosticLogger.Models.Config
{
    public enum ParameterReadMode
    {
        OnChange,
        Periodic,
        OnChangeWithOffset
    }
}
