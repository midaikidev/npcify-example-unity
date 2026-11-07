using System;

namespace Npcify.Sdk.Motion
{
    /// <summary>An engine-neutral vector for live motion math.</summary>
    public struct NpcifyVector3
    {
        public double X;
        public double Y;
        public double Z;

        public NpcifyVector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly NpcifyVector3 Zero = new NpcifyVector3(0, 0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public NpcifyVector3 Normalized()
        {
            var length = Length;
            return length > 1e-12 ? new NpcifyVector3(X / length, Y / length, Z / length) : Zero;
        }

        public static NpcifyVector3 operator +(NpcifyVector3 a, NpcifyVector3 b) => new NpcifyVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static NpcifyVector3 operator -(NpcifyVector3 a, NpcifyVector3 b) => new NpcifyVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static NpcifyVector3 operator *(NpcifyVector3 a, double s) => new NpcifyVector3(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(NpcifyVector3 a, NpcifyVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static NpcifyVector3 Cross(NpcifyVector3 a, NpcifyVector3 b) =>
            new NpcifyVector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static NpcifyVector3 Lerp(NpcifyVector3 a, NpcifyVector3 b, double t) => a + (b - a) * t;

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>An engine-neutral rotation (x, y, z, w) for live motion math.</summary>
    public struct NpcifyQuaternion
    {
        public double X;
        public double Y;
        public double Z;
        public double W;

        public NpcifyQuaternion(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly NpcifyQuaternion Identity = new NpcifyQuaternion(0, 0, 0, 1);

        /// <summary>The rotation <paramref name="b"/> followed by <paramref name="a"/>.</summary>
        public static NpcifyQuaternion operator *(NpcifyQuaternion a, NpcifyQuaternion b) =>
            new NpcifyQuaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public NpcifyQuaternion Normalized()
        {
            var length = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return length > 1e-12 ? new NpcifyQuaternion(X / length, Y / length, Z / length, W / length) : Identity;
        }

        /// <summary>The inverse of a unit rotation.</summary>
        public NpcifyQuaternion Inverse() => new NpcifyQuaternion(-X, -Y, -Z, W);

        public NpcifyVector3 Rotate(NpcifyVector3 v)
        {
            var tx = 2 * (Y * v.Z - Z * v.Y);
            var ty = 2 * (Z * v.X - X * v.Z);
            var tz = 2 * (X * v.Y - Y * v.X);
            return new NpcifyVector3(
                v.X + W * tx + (Y * tz - Z * ty),
                v.Y + W * ty + (Z * tx - X * tz),
                v.Z + W * tz + (X * ty - Y * tx));
        }

        /// <summary>Spherical interpolation along the shorter arc.</summary>
        public static NpcifyQuaternion Slerp(NpcifyQuaternion a, NpcifyQuaternion b, double t)
        {
            if (t <= 0)
            {
                return a;
            }
            if (t >= 1)
            {
                return b;
            }
            var cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (cos < 0)
            {
                cos = -cos;
                b = new NpcifyQuaternion(-b.X, -b.Y, -b.Z, -b.W);
            }
            double wa;
            double wb;
            if (cos > 0.9995)
            {
                wa = 1 - t;
                wb = t;
            }
            else
            {
                var angle = Math.Acos(Math.Min(1, cos));
                var sin = Math.Sin(angle);
                wa = Math.Sin((1 - t) * angle) / sin;
                wb = Math.Sin(t * angle) / sin;
            }
            return new NpcifyQuaternion(
                a.X * wa + b.X * wb,
                a.Y * wa + b.Y * wb,
                a.Z * wa + b.Z * wb,
                a.W * wa + b.W * wb).Normalized();
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###})";
    }
}
