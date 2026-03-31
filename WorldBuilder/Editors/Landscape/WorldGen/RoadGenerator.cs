using System;
using System.Collections.Generic;

namespace WorldBuilder.Editors.Landscape.WorldGen {
    public static class RoadGenerator {
        /// <summary>
        /// Generates a road network connecting all towns via minimum spanning tree,
        /// then A* pathfinds each road segment on the vertex grid with path smoothing.
        /// Returns set of (landblock-local lbX, lbY, vertexIndex) that should have Road bits.
        /// </summary>
        public static HashSet<(ushort lbKey, byte vertexIndex)> Generate(
            List<TownSite> towns, float[,] elevation, float seaLevelNorm,
            WorldGeneratorParams p, Random rng) {

            var roadVertices = new HashSet<(ushort, byte)>();
            if (towns.Count < 2) return roadVertices;

            var edges = BuildMST(towns);

            int verticesX = elevation.GetLength(0);
            int verticesY = elevation.GetLength(1);

            foreach (var (a, b) in edges) {
                int ax = (towns[a].CenterLbX - p.StartX) * 8 + 4;
                int ay = (towns[a].CenterLbY - p.StartY) * 8 + 4;
                int bx = (towns[b].CenterLbX - p.StartX) * 8 + 4;
                int by = (towns[b].CenterLbY - p.StartY) * 8 + 4;

                ax = Math.Clamp(ax, 0, verticesX - 1);
                ay = Math.Clamp(ay, 0, verticesY - 1);
                bx = Math.Clamp(bx, 0, verticesX - 1);
                by = Math.Clamp(by, 0, verticesY - 1);

                var rawPath = AStarPath(elevation, seaLevelNorm, ax, ay, bx, by, verticesX, verticesY);
                var path = SmoothPath(rawPath);
                foreach (var (vx, vy) in path) {
                    AddRoadVertex(roadVertices, vx, vy, p);
                    for (int dx = -1; dx <= 1; dx++) {
                        for (int dy = -1; dy <= 1; dy++) {
                            if (dx == 0 && dy == 0) continue;
                            int nx = vx + dx, ny = vy + dy;
                            if (nx >= 0 && nx < verticesX && ny >= 0 && ny < verticesY)
                                AddRoadVertex(roadVertices, nx, ny, p);
                        }
                    }
                }
            }

            return roadVertices;
        }

        /// <summary>
        /// Marks a road vertex in the result set, including shared boundary vertices
        /// in adjacent landblocks to prevent road breaks at landblock edges.
        /// </summary>
        private static void AddRoadVertex(HashSet<(ushort, byte)> set, int vx, int vy, WorldGeneratorParams p) {
            int lbX = vx / 8;
            int lbY = vy / 8;
            int localX = vx % 8;
            int localY = vy % 8;

            MarkVertex(set, p.StartX + lbX, p.StartY + lbY, localX, localY);

            if (localX == 0 && lbX > 0)
                MarkVertex(set, p.StartX + lbX - 1, p.StartY + lbY, 8, localY);

            if (localY == 0 && lbY > 0)
                MarkVertex(set, p.StartX + lbX, p.StartY + lbY - 1, localX, 8);

            if (localX == 0 && localY == 0 && lbX > 0 && lbY > 0)
                MarkVertex(set, p.StartX + lbX - 1, p.StartY + lbY - 1, 8, 8);
        }

        private static void MarkVertex(HashSet<(ushort, byte)> set, int globalLbX, int globalLbY, int localX, int localY) {
            if (globalLbX < 0 || globalLbX > 254 || globalLbY < 0 || globalLbY > 254) return;
            ushort lbKey = (ushort)((globalLbX << 8) | globalLbY);
            byte vertexIdx = (byte)(localX * 9 + localY);
            if (vertexIdx <= 80)
                set.Add((lbKey, vertexIdx));
        }

        /// <summary>
        /// Smooths an A* path by simplifying with Ramer-Douglas-Peucker then
        /// re-interpolating waypoints via Bresenham lines. Eliminates the staircase
        /// artifacts inherent in grid-based pathfinding.
        /// </summary>
        private static List<(int x, int y)> SmoothPath(List<(int x, int y)> path) {
            if (path.Count <= 2) return path;

            var waypoints = RDPSimplify(path, 0, path.Count - 1, 2.5f);

            var result = new List<(int x, int y)>();
            for (int i = 0; i < waypoints.Count - 1; i++) {
                var line = BresenhamLine(waypoints[i].x, waypoints[i].y,
                    waypoints[i + 1].x, waypoints[i + 1].y);
                int start = (i > 0 && result.Count > 0 && result[^1] == line[0]) ? 1 : 0;
                for (int j = start; j < line.Count; j++)
                    result.Add(line[j]);
            }

            return result;
        }

        private static List<(int x, int y)> RDPSimplify(
            List<(int x, int y)> points, int startIdx, int endIdx, float epsilon) {
            if (endIdx - startIdx <= 1)
                return new List<(int, int)> { points[startIdx], points[endIdx] };

            float maxDist = 0;
            int maxIdx = startIdx;

            for (int i = startIdx + 1; i < endIdx; i++) {
                float d = PerpendicularDistance(points[i], points[startIdx], points[endIdx]);
                if (d > maxDist) {
                    maxDist = d;
                    maxIdx = i;
                }
            }

            if (maxDist > epsilon) {
                var left = RDPSimplify(points, startIdx, maxIdx, epsilon);
                var right = RDPSimplify(points, maxIdx, endIdx, epsilon);
                left.RemoveAt(left.Count - 1);
                left.AddRange(right);
                return left;
            }

            return new List<(int, int)> { points[startIdx], points[endIdx] };
        }

        private static float PerpendicularDistance(
            (int x, int y) point, (int x, int y) lineStart, (int x, int y) lineEnd) {
            float dx = lineEnd.x - lineStart.x;
            float dy = lineEnd.y - lineStart.y;
            float lineLenSq = dx * dx + dy * dy;

            if (lineLenSq < 0.001f) {
                dx = point.x - lineStart.x;
                dy = point.y - lineStart.y;
                return MathF.Sqrt(dx * dx + dy * dy);
            }

            float num = MathF.Abs(dy * point.x - dx * point.y +
                lineEnd.x * lineStart.y - lineEnd.y * lineStart.x);
            return num / MathF.Sqrt(lineLenSq);
        }

        private static List<(int x, int y)> BresenhamLine(int x0, int y0, int x1, int y1) {
            var points = new List<(int, int)>();
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true) {
                points.Add((x0, y0));
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
            return points;
        }

        /// <summary>Kruskal's MST over town indices.</summary>
        private static List<(int a, int b)> BuildMST(List<TownSite> towns) {
            var edges = new List<(float dist, int a, int b)>();
            for (int i = 0; i < towns.Count; i++) {
                for (int j = i + 1; j < towns.Count; j++) {
                    float dx = towns[i].CenterLbX - towns[j].CenterLbX;
                    float dy = towns[i].CenterLbY - towns[j].CenterLbY;
                    edges.Add((MathF.Sqrt(dx * dx + dy * dy), i, j));
                }
            }
            edges.Sort((a, b) => a.dist.CompareTo(b.dist));

            var parent = new int[towns.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

            var mst = new List<(int, int)>();
            foreach (var (_, a, b) in edges) {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) {
                    parent[ra] = rb;
                    mst.Add((a, b));
                    if (mst.Count == towns.Count - 1) break;
                }
            }
            return mst;
        }

        /// <summary>A* pathfinding on the elevation grid with slope-based cost.</summary>
        private static List<(int x, int y)> AStarPath(
            float[,] elevation, float seaLevelNorm,
            int sx, int sy, int gx, int gy, int w, int h) {

            var open = new PriorityQueue<(int x, int y), float>();
            var cameFrom = new Dictionary<(int, int), (int, int)>();
            var gScore = new Dictionary<(int, int), float>();
            var closed = new HashSet<(int, int)>();

            var start = (sx, sy);
            var goal = (gx, gy);
            gScore[start] = 0;
            open.Enqueue(start, Heuristic(sx, sy, gx, gy));

            int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
            int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };
            float[] dCost = { 1.414f, 1f, 1.414f, 1f, 1f, 1.414f, 1f, 1.414f };

            int maxIter = w * h;
            int iter = 0;

            while (open.Count > 0 && iter++ < maxIter) {
                var current = open.Dequeue();
                if (current == goal) break;

                // Skip nodes already settled — prevents redundant re-expansion
                if (!closed.Add(current)) continue;

                float curG = gScore.GetValueOrDefault(current, float.MaxValue);

                for (int d = 0; d < 8; d++) {
                    int nx = current.x + dx[d];
                    int ny = current.y + dy[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    if (closed.Contains((nx, ny))) continue;

                    float slopeCost = MathF.Abs(elevation[nx, ny] - elevation[current.x, current.y]) * 50f;
                    float waterCost = elevation[nx, ny] < seaLevelNorm ? 100f : 0f;
                    float moveCost = dCost[d] + slopeCost + waterCost;
                    float tentG = curG + moveCost;

                    var neighbor = (nx, ny);
                    if (tentG < gScore.GetValueOrDefault(neighbor, float.MaxValue)) {
                        gScore[neighbor] = tentG;
                        cameFrom[neighbor] = current;
                        open.Enqueue(neighbor, tentG + Heuristic(nx, ny, gx, gy));
                    }
                }
            }

            // If goal was never reached return an empty path rather than a garbage
            // partial route that would stamp road bits only near the source town.
            if (!cameFrom.ContainsKey(goal))
                return new List<(int, int)>();

            var path = new List<(int, int)>();
            var pos = goal;
            while (cameFrom.ContainsKey(pos)) {
                path.Add(pos);
                pos = cameFrom[pos];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        private static float Heuristic(int ax, int ay, int bx, int by) {
            return MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        }
    }
}
