#nullable enable
using UnityEngine;

namespace Arena.Presentation.Dice
{
    public static class DicePoseSolver
    {
        public static Quaternion FaceTowardCamera(
            DiceFace face,
            Vector3 directionTowardCamera,
            Vector3 cameraUp)
        {
            Vector3 sourceNormal = SafeNormal(face.OutwardNormal, Vector3.forward);
            Vector3 sourceUp = SafePlanarDirection(face.Upright, sourceNormal);
            Vector3 targetNormal = SafeNormal(directionTowardCamera, Vector3.back);
            Vector3 targetUp = SafePlanarDirection(cameraUp, targetNormal);

            Quaternion sourceBasis = Quaternion.LookRotation(sourceNormal, sourceUp);
            Quaternion targetBasis = Quaternion.LookRotation(targetNormal, targetUp);
            return targetBasis * Quaternion.Inverse(sourceBasis);
        }

        private static Vector3 SafeNormal(Vector3 direction, Vector3 fallback)
        {
            return direction.sqrMagnitude > 0.000001f ? direction.normalized : fallback;
        }

        private static Vector3 SafePlanarDirection(Vector3 direction, Vector3 planeNormal)
        {
            Vector3 planar = Vector3.ProjectOnPlane(direction, planeNormal);
            if (planar.sqrMagnitude > 0.000001f)
                return planar.normalized;

            planar = Vector3.ProjectOnPlane(Vector3.up, planeNormal);
            if (planar.sqrMagnitude > 0.000001f)
                return planar.normalized;

            return Vector3.ProjectOnPlane(Vector3.right, planeNormal).normalized;
        }
    }
}
