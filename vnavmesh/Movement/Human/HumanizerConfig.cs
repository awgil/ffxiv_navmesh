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
    // how many captures one click takes; repeats are what separate a change from run to run spread
    public int RecorderRepeats = 3;

    // --- steering (ADR 0005, ADR 0006) ---

    // master switch: off means upstream behaviour exactly, which is arm 2 of the evaluation
    public bool SteeringEnabled = false;
    // how far ahead along the path to aim, yalms. larger is smoother but cuts corners harder
    public float SteeringLookahead = 8f;
    // ceiling on how fast the commanded heading may rotate, degrees/sec. the character itself
    // tops out near 400, and humans measured at 22-42 through corners
    public float SteeringMaxTurnRate = 45f;
    // ceiling used when recovering: a large correction, or the last stretch of a path. holding the
    // normal limit there makes the character orbit, but removing it entirely snaps at 500+, so this
    // sits between. a turn radius of v/rate must stay under the distance left to cover
    public float SteeringRecoveryTurnRate = 180f;
    // pull the aim point back to where the mesh actually reaches, so cutting a corner cannot aim
    // through geometry. off restores the naive behaviour, which sticks on obstacle heavy routes
    public bool SteeringClampToMesh = true;
}
