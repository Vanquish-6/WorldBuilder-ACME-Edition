using System;
using System.Collections.Generic;
using System.Numerics;
using WorldBuilder.Lib;
using WorldBuilder.Shared.Documents;

namespace WorldBuilder.Editors.Landscape {
    public static class ObjectRaycast {
        public struct ObjectRaycastHit {
            public bool Hit;
            public StaticObject Object;
            public ushort LandblockKey;
            public int ObjectIndex; // Index in LandblockDocument, or -1 for scenery
            public float Distance;
            public Vector3 HitPosition;
            public bool IsScenery;
        }

        /// <summary>
        /// Performs a raycast against all visible static objects in the scene.
        /// Returns the closest hit object, or a miss result.
        /// </summary>
        public static ObjectRaycastHit Raycast(
            float mouseX, float mouseY,
            int viewportWidth, int viewportHeight,
            ICamera camera,
            GameScene scene) {

            var result = new ObjectRaycastHit { Hit = false, Distance = float.MaxValue };

            // Build ray from screen coordinates (same as TerrainRaycast)
            float ndcX = 2.0f * mouseX / viewportWidth - 1.0f;
            float ndcY = 2.0f * mouseY / viewportHeight - 1.0f;

            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 view = camera.GetViewMatrix();

            if (!Matrix4x4.Invert(view * projection, out Matrix4x4 viewProjectionInverse)) {
                return result;
            }

            Vector4 nearPoint = new Vector4(ndcX, ndcY, -1.0f, 1.0f);
            Vector4 farPoint = new Vector4(ndcX, ndcY, 1.0f, 1.0f);

            Vector4 nearWorld = Vector4.Transform(nearPoint, viewProjectionInverse);
            Vector4 farWorld = Vector4.Transform(farPoint, viewProjectionInverse);

            nearWorld /= nearWorld.W;
            farWorld /= farWorld.W;

            Vector3 rayOrigin = new Vector3(nearWorld.X, nearWorld.Y, nearWorld.Z);
            Vector3 rayDirection = Vector3.Normalize(new Vector3(farWorld.X, farWorld.Y, farWorld.Z) - rayOrigin);

            // Test against all static objects from landblock documents
            foreach (var doc in scene._terrainSystem.DocumentManager.ActiveDocs.Values) {
                if (doc is not LandblockDocument lbDoc) continue;

                var lbIdHex = doc.Id.Replace("landblock_", "");
                if (!ushort.TryParse(lbIdHex, System.Globalization.NumberStyles.HexNumber, null, out var lbKey)) continue;

                for (int i = 0; i < lbDoc.StaticObjectCount; i++) {
                    var obj = lbDoc.GetStaticObject(i);
                    var bounds = scene.AnyObjectManager?.GetBounds(obj.Id, obj.IsSetup);
                    if (bounds == null) continue;

                    var (localMin, localMax) = bounds.Value;

                    // Transform bounds to world space
                    var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                        * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                        * Matrix4x4.CreateTranslation(obj.Origin);

                    var worldMin = Vector3.Transform(localMin, worldTransform);
                    var worldMax = Vector3.Transform(localMax, worldTransform);

                    // Ensure min/max are correct after transform
                    var aabbMin = Vector3.Min(worldMin, worldMax);
                    var aabbMax = Vector3.Max(worldMin, worldMax);

                    if (RayIntersectsAABB(rayOrigin, rayDirection, aabbMin, aabbMax, out float dist)) {
                        if (dist < result.Distance) {
                            result = new ObjectRaycastHit {
                                Hit = true,
                                Object = obj,
                                LandblockKey = lbKey,
                                ObjectIndex = i,
                                Distance = dist,
                                HitPosition = rayOrigin + rayDirection * dist,
                                IsScenery = false
                            };
                        }
                    }
                }
            }

            // Also test against scenery objects
            var allStatics = scene.GetAllStaticObjects();
            foreach (var obj in allStatics) {
                var bounds = scene.AnyObjectManager?.GetBounds(obj.Id, obj.IsSetup);
                if (bounds == null) continue;

                var (localMin, localMax) = bounds.Value;

                var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                    * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                    * Matrix4x4.CreateTranslation(obj.Origin);

                var worldMin = Vector3.Transform(localMin, worldTransform);
                var worldMax = Vector3.Transform(localMax, worldTransform);

                var aabbMin = Vector3.Min(worldMin, worldMax);
                var aabbMax = Vector3.Max(worldMin, worldMax);

                if (RayIntersectsAABB(rayOrigin, rayDirection, aabbMin, aabbMax, out float dist)) {
                    if (dist < result.Distance) {
                        // Determine landblock key from position
                        int lbX = (int)Math.Floor(obj.Origin.X / 192f);
                        int lbY = (int)Math.Floor(obj.Origin.Y / 192f);
                        ushort lbKey = (ushort)((lbX << 8) | lbY);

                        result = new ObjectRaycastHit {
                            Hit = true,
                            Object = obj,
                            LandblockKey = lbKey,
                            ObjectIndex = -1, // Scenery objects don't have a document index
                            Distance = dist,
                            HitPosition = rayOrigin + rayDirection * dist,
                            IsScenery = true
                        };
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Performs a marquee (box) select: projects all static objects' bounding boxes to screen space
        /// and returns those whose screen-space footprint overlaps the given rectangle.
        /// </summary>
        public static List<ObjectRaycastHit> BoxSelect(
            Vector2 screenMin, Vector2 screenMax,
            int viewportWidth, int viewportHeight,
            ICamera camera,
            GameScene scene) {

            var results = new List<ObjectRaycastHit>();

            Matrix4x4 view = camera.GetViewMatrix();
            Matrix4x4 projection = camera.GetProjectionMatrix();
            Matrix4x4 viewProjection = view * projection;

            // Normalize the rectangle (handle drag in any direction)
            float rectMinX = MathF.Min(screenMin.X, screenMax.X);
            float rectMaxX = MathF.Max(screenMin.X, screenMax.X);
            float rectMinY = MathF.Min(screenMin.Y, screenMax.Y);
            float rectMaxY = MathF.Max(screenMin.Y, screenMax.Y);

            // Test document objects (non-scenery)
            foreach (var doc in scene._terrainSystem.DocumentManager.ActiveDocs.Values) {
                if (doc is not LandblockDocument lbDoc) continue;

                var lbIdHex = doc.Id.Replace("landblock_", "");
                if (!ushort.TryParse(lbIdHex, System.Globalization.NumberStyles.HexNumber, null, out var lbKey)) continue;

                for (int i = 0; i < lbDoc.StaticObjectCount; i++) {
                    var obj = lbDoc.GetStaticObject(i);

                    // Project the object's bounding box to screen space for accurate hit testing
                    var bounds = scene.AnyObjectManager?.GetBounds(obj.Id, obj.IsSetup);
                    if (bounds == null) {
                        // Fallback: use origin point if no bounds available
                        var screenPos = WorldToScreen(obj.Origin, viewProjection, viewportWidth, viewportHeight);
                        if (screenPos.HasValue &&
                            screenPos.Value.X >= rectMinX && screenPos.Value.X <= rectMaxX &&
                            screenPos.Value.Y >= rectMinY && screenPos.Value.Y <= rectMaxY) {
                            results.Add(new ObjectRaycastHit {
                                Hit = true, Object = obj, LandblockKey = lbKey,
                                ObjectIndex = i, Distance = 0, HitPosition = obj.Origin, IsScenery = false
                            });
                        }
                        continue;
                    }

                    var (localMin, localMax) = bounds.Value;
                    var worldTransform = Matrix4x4.CreateScale(obj.Scale)
                        * Matrix4x4.CreateFromQuaternion(obj.Orientation)
                        * Matrix4x4.CreateTranslation(obj.Origin);

                    // Project all 8 bounding box corners to screen space and compute screen AABB
                    float screenMinX = float.MaxValue, screenMinY = float.MaxValue;
                    float screenMaxX = float.MinValue, screenMaxY = float.MinValue;
                    bool anyVisible = false;

                    for (int cx = 0; cx <= 1; cx++) {
                        for (int cy = 0; cy <= 1; cy++) {
                            for (int cz = 0; cz <= 1; cz++) {
                                var corner = new Vector3(
                                    cx == 0 ? localMin.X : localMax.X,
                                    cy == 0 ? localMin.Y : localMax.Y,
                                    cz == 0 ? localMin.Z : localMax.Z);
                                var worldCorner = Vector3.Transform(corner, worldTransform);
                                var screenCorner = WorldToScreen(worldCorner, viewProjection, viewportWidth, viewportHeight);
                                if (screenCorner.HasValue) {
                                    anyVisible = true;
                                    screenMinX = MathF.Min(screenMinX, screenCorner.Value.X);
                                    screenMinY = MathF.Min(screenMinY, screenCorner.Value.Y);
                                    screenMaxX = MathF.Max(screenMaxX, screenCorner.Value.X);
                                    screenMaxY = MathF.Max(screenMaxY, screenCorner.Value.Y);
                                }
                            }
                        }
                    }

                    if (!anyVisible) continue;

                    // Check if object's screen AABB overlaps the marquee rectangle
                    if (screenMaxX >= rectMinX && screenMinX <= rectMaxX &&
                        screenMaxY >= rectMinY && screenMinY <= rectMaxY) {
                        results.Add(new ObjectRaycastHit {
                            Hit = true,
                            Object = obj,
                            LandblockKey = lbKey,
                            ObjectIndex = i,
                            Distance = 0,
                            HitPosition = obj.Origin,
                            IsScenery = false
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Projects a world-space position to screen-space pixel coordinates.
        /// Returns null if the point is behind the camera.
        /// </summary>
        public static Vector2? WorldToScreen(Vector3 worldPos, Matrix4x4 viewProjection, int viewportWidth, int viewportHeight) {
            var clip = Vector4.Transform(new Vector4(worldPos, 1f), viewProjection);
            if (clip.W <= 0) return null; // Behind camera

            var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
            float screenX = (ndc.X + 1f) * 0.5f * viewportWidth;
            float screenY = (1f - ndc.Y) * 0.5f * viewportHeight; // Y is flipped in screen space
            return new Vector2(screenX, screenY);
        }

        /// <summary>
        /// Ray-AABB intersection test (slab method).
        /// Returns true if the ray intersects the box, with the distance to the hit point.
        /// </summary>
        private static bool RayIntersectsAABB(Vector3 rayOrigin, Vector3 rayDir, Vector3 aabbMin, Vector3 aabbMax, out float distance) {
            distance = 0f;

            float tMin = float.MinValue;
            float tMax = float.MaxValue;

            for (int i = 0; i < 3; i++) {
                float origin = i == 0 ? rayOrigin.X : i == 1 ? rayOrigin.Y : rayOrigin.Z;
                float dir = i == 0 ? rayDir.X : i == 1 ? rayDir.Y : rayDir.Z;
                float min = i == 0 ? aabbMin.X : i == 1 ? aabbMin.Y : aabbMin.Z;
                float max = i == 0 ? aabbMax.X : i == 1 ? aabbMax.Y : aabbMax.Z;

                if (Math.Abs(dir) < 1e-8f) {
                    // Ray is parallel to this slab
                    if (origin < min || origin > max) return false;
                }
                else {
                    float t1 = (min - origin) / dir;
                    float t2 = (max - origin) / dir;

                    if (t1 > t2) (t1, t2) = (t2, t1);

                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);

                    if (tMin > tMax) return false;
                }
            }

            distance = tMin >= 0 ? tMin : tMax;
            return distance >= 0;
        }
    }
}
