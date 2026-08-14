namespace Navmesh.Movement.Human;

// all fork-added settings live here, hung off Config as a single field; see ADR 0002
// everything defaults to upstream behaviour
public class HumanizerConfig
{
    // --- recorder (ADR 0003) ---

    // how close to A counts as arrived, yalms
    public float RecorderArriveTolerance = 1.5f;
    // how close to B ends the capture, yalms
    public float RecorderFinishTolerance = 2.5f;
    // give up on a leg after this long, seconds
    public int RecorderTimeoutSec = 300;
    // input magnitude that counts as "departed" and starts recording
    public float RecorderDepartThreshold = 0.05f;
    // draw the active route's A and B in the world
    public bool RecorderDrawRoute = true;
}
