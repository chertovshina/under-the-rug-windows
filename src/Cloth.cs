using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Media3D;

namespace RugWindows
{
    // Port of the original Under the Rug XPBD cloth solver. Coordinates are in
    // desktop DIPs: x right, y down, z toward the viewer. The renderer negates y.
    public sealed class Cloth
    {
        private const int Substeps = 10;
        private const double Gravity = 5000.0;
        private const double AirDamping = 1.3;
        private const double ShearCompliance = 2e-7;
        private const double BendCompliance = 3e-7;
        private const double StaticFriction = 2.0;
        private const double KineticFriction = 1.3;
        private const double SelfFriction = 0.3;

        private struct Constraint
        {
            public int A, B;
            public double Rest, Compliance;
            public Constraint(int a, int b, double rest, double compliance)
            {
                A = a; B = b; Rest = rest; Compliance = compliance;
            }
        }

        public double Width { get; private set; }
        public double Height { get; private set; }
        public double Spacing { get; private set; }
        public double MaxSpeed { get; private set; }
        // End-to-end motion in DIPs over the last frame. Contact constraints can
        // leave a substep velocity at rest, so these are better sleep metrics.
        public double MaxFrameMotion { get; private set; }
        public double AverageFrameMotion { get; private set; }
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public int Count { get; private set; }
        public Point3D[] Positions { get; private set; }
        public int[] Indices { get; private set; }
        public Point[] UVs { get; private set; }
        public Func<double, double, double> Ground { get; set; }
        public bool IsGrabbing { get { return grabCount != 0; } }

        private readonly double thickness;
        private readonly double cameraDistance;
        private readonly Point cameraCenter;
        private readonly Point3D[] rest;
        private readonly Point3D[] previous;
        private readonly Point3D[] frameStart;
        private readonly Vector3D[] velocity;
        private readonly double[] inverseMass;
        private readonly Point[] projected;
        private readonly Constraint[] constraints;
        private readonly SpatialHash spatialHash;
        private readonly int[] grabbedIds = new int[9];
        private readonly Vector3D[] grabOffsets = new Vector3D[9];
        private readonly double[] tetherRest;
        private readonly Random random = new Random();
        private int grabCount;
        private Point3D grabPosition;
        private Point3D grabTarget;
        private Point lastGoodCenter;

        public Cloth(double width, double height, int columns, double viewportWidth, double viewportHeight)
        {
            if (!Finite(width) || width < 1) throw new ArgumentOutOfRangeException("width");
            if (!Finite(height) || height < 1) throw new ArgumentOutOfRangeException("height");
            if (columns < 3 || columns > 256) throw new ArgumentOutOfRangeException("columns");
            if (!Finite(viewportWidth) || viewportWidth <= 0) throw new ArgumentOutOfRangeException("viewportWidth");
            if (!Finite(viewportHeight) || viewportHeight <= 0) throw new ArgumentOutOfRangeException("viewportHeight");
            Width = width;
            Columns = columns;
            Spacing = width / (columns - 1);
            double requestedRows = Math.Round(height / Spacing) + 1;
            if (requestedRows > 256) throw new ArgumentOutOfRangeException("height", "The cloth grid may have at most 256 rows.");
            Rows = Math.Max(3, (int)requestedRows);
            Height = Spacing * (Rows - 1);
            Count = Columns * Rows;
            thickness = Spacing * 1.1;
            cameraDistance = Math.Max(viewportWidth, viewportHeight) * 1.15;
            cameraCenter = new Point(viewportWidth * 0.5, viewportHeight * 0.5);
            lastGoodCenter = cameraCenter;
            Positions = new Point3D[Count];
            previous = new Point3D[Count];
            frameStart = new Point3D[Count];
            velocity = new Vector3D[Count];
            rest = new Point3D[Count];
            inverseMass = new double[Count];
            projected = new Point[Count];
            tetherRest = new double[Count];
            UVs = new Point[Count];
            spatialHash = new SpatialHash(thickness, Count);
            List<int> triangles = new List<int>((Columns - 1) * (Rows - 1) * 6);
            List<Constraint> links = new List<Constraint>(Count * 6);
            for (int j = 0; j < Rows; j++)
            {
                for (int i = 0; i < Columns; i++)
                {
                    int k = j * Columns + i;
                    rest[k] = new Point3D(i * Spacing - Width * 0.5, j * Spacing - Height * 0.5, 0);
                    Positions[k] = previous[k] = rest[k];
                    inverseMass[k] = 1;
                    UVs[k] = new Point((double)i / (Columns - 1), (double)j / (Rows - 1));
                }
            }
            for (int j = 0; j < Rows; j++)
            {
                for (int i = 0; i < Columns; i++)
                {
                    int a = j * Columns + i;
                    if (i + 1 < Columns) AddConstraint(links, a, a + 1, 0);
                    if (j + 1 < Rows) AddConstraint(links, a, a + Columns, 0);
                    if (i + 1 < Columns && j + 1 < Rows)
                    {
                        int b = a + 1, c = a + Columns, d = c + 1;
                        AddConstraint(links, a, d, ShearCompliance);
                        AddConstraint(links, b, c, ShearCompliance);
                        // Reverse Swift's winding: the WPF mesh uses -y.
                        if (((i + j) & 1) == 0) triangles.AddRange(new int[] { a, d, b, a, c, d });
                        else triangles.AddRange(new int[] { a, c, b, b, c, d });
                    }
                    if (i + 2 < Columns) AddConstraint(links, a, a + 2, BendCompliance);
                    if (j + 2 < Rows) AddConstraint(links, a, a + Columns * 2, BendCompliance);
                }
            }
            Indices = triangles.ToArray();
            constraints = links.ToArray();
        }

        public Point Center
        {
            get
            {
                double x = 0, y = 0;
                for (int i = 0; i < Count; i++) { x += Positions[i].X; y += Positions[i].Y; }
                return new Point(x / Count, y / Count);
            }
        }

        public double Angle
        {
            get
            {
                int k = (Rows / 2) * Columns;
                Point3D a = Positions[k], b = Positions[k + Columns - 1];
                return Math.Atan2(b.Y - a.Y, b.X - a.X);
            }
        }

        public void Layout(Point center, double angle, double drop)
        {
            EndGrab();
            if (!Finite(center.X) || !Finite(center.Y)) center = cameraCenter;
            if (!Finite(angle)) angle = 0;
            if (!Finite(drop)) drop = 60;
            drop = Math.Max(0, Math.Min(drop, cameraDistance * 0.6));
            double ca = Math.Cos(angle), sa = Math.Sin(angle);
            for (int i = 0; i < Count; i++)
            {
                Point3D r = rest[i];
                double u = (double)(i % Columns) / (Columns - 1);
                double z = drop > 0 ? Math.Max(0, drop * (0.55 + 0.45 * u) + random.NextDouble() * 3 - 1.5) : 0;
                Positions[i] = new Point3D(center.X + r.X * ca - r.Y * sa, center.Y + r.X * sa + r.Y * ca, z);
                previous[i] = Positions[i];
                velocity[i] = new Vector3D();
            }
            lastGoodCenter = center;
            MaxSpeed = 1000;
            MaxFrameMotion = AverageFrameMotion = 1000;
        }

        public void Rotate(double radians)
        {
            if (!Finite(radians)) return;
            Point center = Center;
            double ca = Math.Cos(radians), sa = Math.Sin(radians);
            for (int i = 0; i < Count; i++)
            {
                Positions[i] = RotatePoint(Positions[i], center, ca, sa);
                previous[i] = RotatePoint(previous[i], center, ca, sa);
                velocity[i] = RotateVector(velocity[i], ca, sa);
            }
            if (IsGrabbing)
            {
                grabPosition = RotatePoint(grabPosition, center, ca, sa);
                grabTarget = RotatePoint(grabTarget, center, ca, sa);
                for (int i = 0; i < grabCount; i++) grabOffsets[i] = RotateVector(grabOffsets[i], ca, sa);
            }
            MaxSpeed = Math.Max(MaxSpeed, 10);
        }

        public Point Project(Point3D p)
        {
            double w = Math.Max(0.05, 1 - p.Z / cameraDistance);
            return new Point(cameraCenter.X + (p.X - cameraCenter.X) / w, cameraCenter.Y + (p.Y - cameraCenter.Y) / w);
        }

        private Point3D Unproject(Point screen, double z)
        {
            double w = 1 - z / cameraDistance;
            return new Point3D(cameraCenter.X + (screen.X - cameraCenter.X) * w, cameraCenter.Y + (screen.Y - cameraCenter.Y) * w, z);
        }

        // Intersect the projected triangles and choose the frontmost surface,
        // then the nearest vertex of that triangle. Empty desktop returns -1.
        public int Pick(Point screen)
        {
            if (!Finite(screen.X) || !Finite(screen.Y)) return -1;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < Count; i++)
            {
                Point p = projected[i] = Project(Positions[i]);
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            if (screen.X < minX || screen.Y < minY || screen.X > maxX || screen.Y > maxY) return -1;
            int bestVertex = -1;
            double bestZ = double.MinValue;
            for (int k = 0; k < Indices.Length; k += 3)
            {
                int a = Indices[k], b = Indices[k + 1], c = Indices[k + 2];
                Point pa = projected[a], pb = projected[b], pc = projected[c];
                if (screen.X < Math.Min(pa.X, Math.Min(pb.X, pc.X)) || screen.X > Math.Max(pa.X, Math.Max(pb.X, pc.X))) continue;
                if (screen.Y < Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)) || screen.Y > Math.Max(pa.Y, Math.Max(pb.Y, pc.Y))) continue;
                double x0 = pb.X - pa.X, y0 = pb.Y - pa.Y, x1 = pc.X - pa.X, y1 = pc.Y - pa.Y;
                double x2 = screen.X - pa.X, y2 = screen.Y - pa.Y;
                double den = x0 * y1 - x1 * y0;
                if (Math.Abs(den) < 1e-8) continue;
                double u = (x2 * y1 - x1 * y2) / den, v = (x0 * y2 - x2 * y0) / den;
                if (u < -1e-7 || v < -1e-7 || u + v > 1.0000001) continue;
                double w = 1 - u - v;
                double z = Positions[a].Z * w + Positions[b].Z * u + Positions[c].Z * v;
                if (z <= bestZ) continue;
                bestZ = z;
                bestVertex = w >= u && w >= v ? a : (u >= v ? b : c);
            }
            return bestVertex;
        }

        // lift is the distance above the picked surface, in desktop DIPs.
        public void BeginGrab(int vertex, Point screen, double lift)
        {
            EndGrab();
            if (vertex < 0 || vertex >= Count || !Finite(screen.X) || !Finite(screen.Y)) return;
            if (!Finite(lift)) lift = 120;
            int vi = vertex % Columns, vj = vertex / Columns;
            Point3D center = Positions[vertex];
            for (int j = Math.Max(0, vj - 1); j <= Math.Min(Rows - 1, vj + 1); j++)
            {
                for (int i = Math.Max(0, vi - 1); i <= Math.Min(Columns - 1, vi + 1); i++)
                {
                    int k = j * Columns + i;
                    Vector3D offset = Positions[k] - center;
                    offset.Z = 0;
                    grabbedIds[grabCount] = k;
                    grabOffsets[grabCount++] = offset;
                    inverseMass[k] = 0;
                }
            }
            grabPosition = center;
            grabTarget = Unproject(screen, Math.Max(0, Math.Min(cameraDistance * 0.65, Math.Max(center.Z, 0) + lift)));
            for (int i = 0; i < Count; i++) tetherRest[i] = (rest[i] - rest[vertex]).Length * 1.01;
            MaxSpeed = 1000;
        }

        public void MoveGrab(Point screen)
        {
            if (IsGrabbing && Finite(screen.X) && Finite(screen.Y)) grabTarget = Unproject(screen, grabTarget.Z);
        }

        public void EndGrab()
        {
            for (int i = 0; i < grabCount; i++)
            {
                int k = grabbedIds[i];
                inverseMass[k] = 1;
                double speed = velocity[k].Length;
                if (speed > 1400) velocity[k] *= 1400 / speed;
            }
            grabCount = 0;
        }

        public void Step(double dt)
        {
            if (!Finite(dt) || dt <= 0) return;
            // Long UI pauses must not create a single enormous physics step.
            dt = Math.Min(dt, 1.0 / 30);
            if (!StateIsFinite()) { Recover(); return; }
            Array.Copy(Positions, frameStart, Count);
            double sdt = dt / Substeps;
            double maxVelocity = 0.2 * thickness / sdt;
            double travel = Math.Min(Math.Max(MaxSpeed * dt * 1.5, thickness), thickness * 4);
            spatialHash.Build(Positions, travel);
            for (int step = 0; step < Substeps; step++)
            {
                Integrate(sdt, maxVelocity);
                MoveGrabbed(sdt);
                SolveConstraints(sdt);
                SolveTethers();
                SolveSelfCollisions();
                SolveSelfCollisions();
                SolveGround();
                for (int i = 0; i < Count; i++) velocity[i] = (Positions[i] - previous[i]) / sdt;
            }
            double maxSpeedSquared = 0;
            for (int i = 0; i < Count; i++) if (inverseMass[i] > 0) maxSpeedSquared = Math.Max(maxSpeedSquared, velocity[i].LengthSquared);
            MaxSpeed = Math.Sqrt(maxSpeedSquared);
            if (!StateIsFinite() || !Finite(MaxSpeed)) Recover();
            else
            {
                double maximum = 0, total = 0;
                for (int i = 0; i < Count; i++)
                {
                    double move = (Positions[i] - frameStart[i]).Length;
                    maximum = Math.Max(maximum, move);
                    total += move;
                }
                MaxFrameMotion = maximum;
                AverageFrameMotion = total / Count;
                lastGoodCenter = Center;
            }
        }

        private void AddConstraint(List<Constraint> links, int a, int b, double compliance)
        {
            links.Add(new Constraint(a, b, (rest[a] - rest[b]).Length, compliance));
        }

        private void Integrate(double dt, double maxVelocity)
        {
            double damp = Math.Max(0, 1 - AirDamping * dt), maxSquared = maxVelocity * maxVelocity;
            for (int i = 0; i < Count; i++)
            {
                if (inverseMass[i] == 0) continue;
                Vector3D v = velocity[i];
                v.Z -= Gravity * dt;
                v *= damp;
                double speedSquared = v.LengthSquared;
                if (speedSquared > maxSquared) v *= maxVelocity / Math.Sqrt(speedSquared);
                velocity[i] = v;
                previous[i] = Positions[i];
                Positions[i] += v * dt;
            }
        }

        private void MoveGrabbed(double dt)
        {
            if (!IsGrabbing) return;
            Vector3D delta = (grabTarget - grabPosition) * Math.Min(1, dt * 30);
            double distance = delta.Length, limit = 0.3 * thickness;
            if (distance > limit) delta *= limit / distance;
            grabPosition += delta;
            for (int i = 0; i < grabCount; i++)
            {
                int k = grabbedIds[i];
                previous[k] = Positions[k];
                Positions[k] = grabPosition + grabOffsets[i];
            }
        }

        private void SolveConstraints(double dt)
        {
            double inverseDtSquared = 1 / (dt * dt);
            for (int i = 0; i < constraints.Length; i++)
            {
                Constraint c = constraints[i];
                double wa = inverseMass[c.A], wb = inverseMass[c.B], w = wa + wb;
                if (w == 0) continue;
                Vector3D delta = Positions[c.B] - Positions[c.A];
                double length = delta.Length;
                if (length < 1e-6) continue;
                double s = -(length - c.Rest) / (w + c.Compliance * inverseDtSquared);
                delta *= s / length;
                Positions[c.A] -= delta * wa;
                Positions[c.B] += delta * wb;
            }
        }

        private void SolveTethers()
        {
            if (!IsGrabbing) return;
            for (int i = 0; i < Count; i++)
            {
                if (inverseMass[i] == 0) continue;
                Vector3D delta = Positions[i] - grabPosition;
                double length = delta.Length;
                if (length > tetherRest[i]) Positions[i] = grabPosition + delta * (tetherRest[i] / length);
            }
        }

        private void SolveSelfCollisions()
        {
            double thicknessSquared = thickness * thickness;
            for (int i = 0; i < Count; i++)
            {
                double wi = inverseMass[i];
                for (int k = spatialHash.First[i]; k < spatialHash.First[i + 1]; k++)
                {
                    int j = spatialHash.Neighbors[k];
                    double wj = inverseMass[j], w = wi + wj;
                    if (w == 0) continue;
                    Vector3D delta = Positions[j] - Positions[i];
                    double distanceSquared = delta.LengthSquared;
                    if (distanceSquared > thicknessSquared || distanceSquared < 1e-16) continue;
                    double restDistanceSquared = (rest[i] - rest[j]).LengthSquared;
                    if (distanceSquared > restDistanceSquared) continue;
                    double minDistance = restDistanceSquared < thicknessSquared ? Math.Sqrt(restDistanceSquared) : thickness;
                    double distance = Math.Sqrt(distanceSquared);
                    delta *= (minDistance - distance) / (distance * w);
                    Positions[i] -= delta * wi;
                    Positions[j] += delta * wj;
                    Vector3D v0 = Positions[i] - previous[i], v1 = Positions[j] - previous[j];
                    Vector3D average = (v0 * wj + v1 * wi) / w;
                    Positions[i] += (average - v0) * (SelfFriction * wi / w * 2);
                    Positions[j] += (average - v1) * (SelfFriction * wj / w * 2);
                }
            }
        }

        private void SolveGround()
        {
            Func<double, double, double> ground = Ground;
            for (int i = 0; i < Count; i++)
            {
                if (inverseMass[i] == 0) continue;
                Point3D p = Positions[i];
                double height = ground == null ? 0 : ground(p.X, p.Y);
                if (!Finite(height)) height = 0;
                if (p.Z >= height) continue;
                double penetration = height - p.Z;
                p.Z = height;
                Point3D q = previous[i];
                double dx = p.X - q.X, dy = p.Y - q.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < StaticFriction * penetration) { p.X = q.X; p.Y = q.Y; }
                else if (length > 0)
                {
                    double factor = Math.Min(KineticFriction * penetration / length, 1);
                    p.X -= dx * factor; p.Y -= dy * factor;
                }
                // A height-field slope can change height when friction moves x/y.
                if (ground != null)
                {
                    double finalHeight = ground(p.X, p.Y);
                    if (Finite(finalHeight)) p.Z = Math.Max(p.Z, finalHeight);
                }
                Positions[i] = p;
            }
        }

        private bool StateIsFinite()
        {
            for (int i = 0; i < Count; i++)
            {
                Point3D p = Positions[i];
                if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z) || Math.Abs(p.X) > 1e8 || Math.Abs(p.Y) > 1e8 || Math.Abs(p.Z) > 1e8) return false;
            }
            return true;
        }

        private void Recover() { Layout(lastGoodCenter, 0, 60); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static Vector3D RotateVector(Vector3D v, double ca, double sa) { return new Vector3D(v.X * ca - v.Y * sa, v.X * sa + v.Y * ca, v.Z); }
        private static Point3D RotatePoint(Point3D p, Point center, double ca, double sa)
        {
            double x = p.X - center.X, y = p.Y - center.Y;
            return new Point3D(center.X + x * ca - y * sa, center.Y + x * sa + y * ca, p.Z);
        }

        // Compact reusable spatial hash, adapted from the original's Ten Minute
        // Physics hash. A stamp removes duplicate neighbors from hash collisions.
        private sealed class SpatialHash
        {
            private readonly double cellSize;
            private readonly int tableSize;
            private readonly int[] starts, entries, seen;
            private int stamp;
            public readonly int[] First;
            public int[] Neighbors;

            public SpatialHash(double spacing, int count)
            {
                cellSize = spacing;
                tableSize = count * 2;
                starts = new int[tableSize + 1];
                entries = new int[count];
                seen = new int[count];
                First = new int[count + 1];
                Neighbors = new int[count * 24];
            }

            private int Coordinate(double c)
            {
                double value = Math.Floor(c / cellSize);
                return (int)Math.Max(-1000000000, Math.Min(1000000000, value));
            }

            private int Hash(int x, int y, int z)
            {
                uint value = unchecked((uint)((x * 92837111) ^ (y * 689287499) ^ (z * 283923481)));
                return (int)(value % (uint)tableSize);
            }

            private int Hash(Point3D p) { return Hash(Coordinate(p.X), Coordinate(p.Y), Coordinate(p.Z)); }

            public void Build(Point3D[] positions, double distance)
            {
                Array.Clear(starts, 0, starts.Length);
                for (int i = 0; i < positions.Length; i++) starts[Hash(positions[i])]++;
                int sum = 0;
                for (int i = 0; i < tableSize; i++) { sum += starts[i]; starts[i] = sum; }
                starts[tableSize] = sum;
                for (int i = 0; i < positions.Length; i++) entries[--starts[Hash(positions[i])]] = i;
                int count = 0;
                double squared = distance * distance;
                for (int i = 0; i < positions.Length; i++)
                {
                    First[i] = count;
                    if (stamp == int.MaxValue) { Array.Clear(seen, 0, seen.Length); stamp = 0; }
                    stamp++;
                    Point3D p = positions[i];
                    int x0 = Coordinate(p.X - distance), x1 = Coordinate(p.X + distance);
                    int y0 = Coordinate(p.Y - distance), y1 = Coordinate(p.Y + distance);
                    int z0 = Coordinate(p.Z - distance), z1 = Coordinate(p.Z + distance);
                    for (int x = x0; x <= x1; x++)
                    {
                        for (int y = y0; y <= y1; y++)
                        {
                            for (int z = z0; z <= z1; z++)
                            {
                                int bucket = Hash(x, y, z);
                                for (int k = starts[bucket]; k < starts[bucket + 1]; k++)
                                {
                                    int j = entries[k];
                                    if (j >= i || seen[j] == stamp) continue;
                                    seen[j] = stamp;
                                    if ((positions[j] - p).LengthSquared > squared) continue;
                                    if (count == Neighbors.Length) Array.Resize(ref Neighbors, Neighbors.Length * 2);
                                    Neighbors[count++] = j;
                                }
                            }
                        }
                    }
                }
                First[positions.Length] = count;
            }
        }
    }
}
