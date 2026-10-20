using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

using static CombatSolver.SearchProbeHelpers;

internal sealed partial class CombatBeamSolver
{
    internal bool DisableExecutionChoiceContinuationsForTesting
    { init => _disableExecutionChoiceContinuationsForTesting = value; }

}
