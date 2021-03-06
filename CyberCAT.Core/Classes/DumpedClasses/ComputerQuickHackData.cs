using CyberCAT.Core.Classes.Mapping;
using CyberCAT.Core.Classes.NodeRepresentations;

namespace CyberCAT.Core.Classes.DumpedClasses
{
    [RealName("ComputerQuickHackData")]
    public class ComputerQuickHackData : GenericUnknownStruct.BaseClassEntry
    {
        [RealName("alternativeName")]
        public TweakDbId AlternativeName { get; set; }
        
        [RealName("factName")]
        public CName FactName { get; set; }
        
        [RealName("factValue")]
        public int FactValue { get; set; }
        
        [RealName("operationType")]
        public DumpedEnums.EMathOperationType? OperationType { get; set; }
    }
}
