// Minimum-area bounding rectangle, replacing Emgu.CV's CvInvoke.MinAreaRect so the exporter runs
// without OpenCV's native runtime (which Emgu only ships for Windows in the package used here).
//
// This is a line-by-line port of OpenCV 4.10.0 (the version Emgu.CV 4.10 wraps):
//   modules/imgproc/src/convhull.cpp   convexHull (Sklansky), clockwise=false, returnPoints=true
//   modules/imgproc/src/rotcalipers.cpp rotatingCalipers + minAreaRect
//   modules/core/src/types.cpp         RotatedRect::points
// It keeps OpenCV's float/double casts and tie-breaking so door boxes come out the same as the
// Windows build. OpenCV 4.x is licensed under the Apache License 2.0.

using System.Drawing;

namespace CEM.Client.ZoneExporter
{
    internal struct RotatedRect
    {
        public PointF Center;
        public SizeF Size;
        public float Angle; // degrees

        /// <summary>Corners in the order of OpenCV's RotatedRect::points (Emgu GetVertices).</summary>
        public PointF[] GetVertices()
        {
            double angle = Angle * Math.PI / 180.0;
            float b = (float)Math.Cos(angle) * 0.5f;
            float a = (float)Math.Sin(angle) * 0.5f;

            var pt = new PointF[4];
            pt[0].X = Center.X - a * Size.Height - b * Size.Width;
            pt[0].Y = Center.Y + b * Size.Height - a * Size.Width;
            pt[1].X = Center.X + a * Size.Height - b * Size.Width;
            pt[1].Y = Center.Y - b * Size.Height - a * Size.Width;
            pt[2].X = 2 * Center.X - pt[0].X;
            pt[2].Y = 2 * Center.Y - pt[0].Y;
            pt[3].X = 2 * Center.X - pt[1].X;
            pt[3].Y = 2 * Center.Y - pt[1].Y;
            return pt;
        }
    }

    internal static class MinAreaRect
    {
        public static RotatedRect Compute(PointF[] points)
        {
            var box = new RotatedRect();
            PointF[] hull = ConvexHull(points);
            int n = hull.Length;

            if (n > 2)
            {
                PointF[] o = RotatingCalipersMinAreaRect(hull);
                box.Center.X = o[0].X + (o[1].X + o[2].X) * 0.5f;
                box.Center.Y = o[0].Y + (o[1].Y + o[2].Y) * 0.5f;
                box.Size.Width = (float)Math.Sqrt((double)o[1].X * o[1].X + (double)o[1].Y * o[1].Y);
                box.Size.Height = (float)Math.Sqrt((double)o[2].X * o[2].X + (double)o[2].Y * o[2].Y);
                box.Angle = (float)Math.Atan2((double)o[1].Y, (double)o[1].X);
            }
            else if (n == 2)
            {
                box.Center.X = (hull[0].X + hull[1].X) * 0.5f;
                box.Center.Y = (hull[0].Y + hull[1].Y) * 0.5f;
                double dx = hull[1].X - hull[0].X;
                double dy = hull[1].Y - hull[0].Y;
                box.Size.Width = (float)Math.Sqrt(dx * dx + dy * dy);
                box.Size.Height = 0;
                box.Angle = (float)Math.Atan2(dy, dx);
            }
            else if (n == 1)
            {
                box.Center = hull[0];
            }

            box.Angle = (float)(box.Angle * 180 / Math.PI);
            return box;
        }

        // ---- convexHull (float path, clockwise = false) --------------------------------------

        private static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        // 'order' holds indices into 'pts' sorted like OpenCV's pointer array.
        private static int Sklansky(PointF[] pts, int[] order, int start, int end, int[] stack, int stackOff, int nsign, int sign2)
        {
            int incr = end > start ? 1 : -1;
            int pprev = start, pcur = pprev + incr, pnext = pcur + incr;
            int stacksize = 3;

            if (start == end ||
                (pts[order[start]].X == pts[order[end]].X && pts[order[start]].Y == pts[order[end]].Y))
            {
                stack[stackOff + 0] = start;
                return 1;
            }

            stack[stackOff + 0] = pprev;
            stack[stackOff + 1] = pcur;
            stack[stackOff + 2] = pnext;

            end += incr;

            while (pnext != end)
            {
                float cury = pts[order[pcur]].Y;
                float nexty = pts[order[pnext]].Y;
                float by = nexty - cury;

                if (Sign(by) != nsign)
                {
                    float ax = pts[order[pcur]].X - pts[order[pprev]].X;
                    float bx = pts[order[pnext]].X - pts[order[pcur]].X;
                    float ay = cury - pts[order[pprev]].Y;
                    double convexity = (double)ay * bx - (double)ax * by;

                    if (Sign(convexity) == sign2 && (ax != 0 || ay != 0))
                    {
                        pprev = pcur;
                        pcur = pnext;
                        pnext += incr;
                        stack[stackOff + stacksize] = pnext;
                        stacksize++;
                    }
                    else
                    {
                        if (pprev == start)
                        {
                            pcur = pnext;
                            stack[stackOff + 1] = pcur;
                            pnext += incr;
                            stack[stackOff + 2] = pnext;
                        }
                        else
                        {
                            stack[stackOff + stacksize - 2] = pnext;
                            pcur = pprev;
                            pprev = stack[stackOff + stacksize - 4];
                            stacksize--;
                        }
                    }
                }
                else
                {
                    pnext += incr;
                    stack[stackOff + stacksize - 1] = pnext;
                }
            }

            return --stacksize;
        }

        internal static PointF[] ConvexHull(PointF[] pts)
        {
            int total = pts.Length;
            if (total == 0)
                return [];

            int[] order = new int[total];
            for (int i = 0; i < total; i++)
                order[i] = i;
            // CHullCmpPoints: x, then y, then original position (pointer address).
            Array.Sort(order, (i1, i2) =>
            {
                if (pts[i1].X != pts[i2].X) return pts[i1].X < pts[i2].X ? -1 : 1;
                if (pts[i1].Y != pts[i2].Y) return pts[i1].Y < pts[i2].Y ? -1 : 1;
                return i1.CompareTo(i2);
            });

            int miny_ind = 0, maxy_ind = 0;
            for (int i = 1; i < total; i++)
            {
                float y = pts[order[i]].Y;
                if (pts[order[miny_ind]].Y > y) miny_ind = i;
                if (pts[order[maxy_ind]].Y < y) maxy_ind = i;
            }

            int[] stack = new int[total + 2];
            int[] hullbuf = new int[total];
            int nout = 0;

            if (pts[order[0]].X == pts[order[total - 1]].X && pts[order[0]].Y == pts[order[total - 1]].Y)
            {
                hullbuf[nout++] = 0;
            }
            else
            {
                // upper half
                int tlOff = 0;
                int tl_count = Sklansky(pts, order, 0, maxy_ind, stack, tlOff, -1, 1);
                int trOff = tl_count;
                int tr_count = Sklansky(pts, order, total - 1, maxy_ind, stack, trOff, -1, -1);

                // clockwise == false
                (tlOff, trOff) = (trOff, tlOff);
                (tl_count, tr_count) = (tr_count, tl_count);

                for (int i = 0; i < tl_count - 1; i++)
                    hullbuf[nout++] = order[stack[tlOff + i]];
                for (int i = tr_count - 1; i > 0; i--)
                    hullbuf[nout++] = order[stack[trOff + i]];
                int stop_idx = tr_count > 2 ? stack[trOff + 1] : tl_count > 2 ? stack[tlOff + tl_count - 2] : -1;

                // lower half
                int blOff = 0;
                int bl_count = Sklansky(pts, order, 0, miny_ind, stack, blOff, 1, -1);
                int brOff = bl_count;
                int br_count = Sklansky(pts, order, total - 1, miny_ind, stack, brOff, 1, 1);

                if (stop_idx >= 0)
                {
                    int check_idx = bl_count > 2 ? stack[blOff + 1] :
                        bl_count + br_count > 2 ? stack[brOff + 2 - bl_count] : -1;
                    if (check_idx == stop_idx || (check_idx >= 0 &&
                        pts[order[check_idx]].X == pts[order[stop_idx]].X &&
                        pts[order[check_idx]].Y == pts[order[stop_idx]].Y))
                    {
                        bl_count = Math.Min(bl_count, 2);
                        br_count = Math.Min(br_count, 2);
                    }
                }

                for (int i = 0; i < bl_count - 1; i++)
                    hullbuf[nout++] = order[stack[blOff + i]];
                for (int i = br_count - 1; i > 0; i--)
                    hullbuf[nout++] = order[stack[brOff + i]];

                // cyclic shift so indices ascend or descend, as OpenCV does
                if (nout >= 3)
                {
                    int min_idx = 0, max_idx = 0, lt = 0;
                    for (int i = 1; i < nout; i++)
                    {
                        int idx = hullbuf[i];
                        lt += hullbuf[i - 1] < idx ? 1 : 0;
                        if (lt > 1 && lt <= i - 2)
                            break;
                        if (idx < hullbuf[min_idx]) min_idx = i;
                        if (idx > hullbuf[max_idx]) max_idx = i;
                    }
                    int mmdist = Math.Abs(max_idx - min_idx);
                    if ((mmdist == 1 || mmdist == nout - 1) && (lt <= 1 || lt >= nout - 2))
                    {
                        bool ascending = (max_idx + 1) % nout == min_idx;
                        int i0 = ascending ? min_idx : max_idx, j = i0;
                        if (i0 > 0)
                        {
                            int i;
                            for (i = 0; i < nout; i++)
                            {
                                int curr_idx = stack[i] = hullbuf[j];
                                int next_j = j + 1 < nout ? j + 1 : 0;
                                int next_idx = hullbuf[next_j];
                                if (i < nout - 1 && (ascending != (curr_idx < next_idx)))
                                    break;
                                j = next_j;
                            }
                            if (i == nout)
                                Array.Copy(stack, hullbuf, nout);
                        }
                    }
                }
            }

            var hull = new PointF[nout];
            for (int i = 0; i < nout; i++)
                hull[i] = pts[hullbuf[i]];
            return hull;
        }

        // ---- rotatingCalipers, CALIPERS_MINAREARECT ------------------------------------------

        private static PointF Rot90CCW(PointF v) => new(-v.Y, v.X);
        private static PointF Rot90CW(PointF v) => new(v.Y, -v.X);
        private static PointF Rot180(PointF v) => new(-v.X, -v.Y);

        private static bool FirstVecIsRight(PointF vec1, PointF vec2)
        {
            PointF tmp = Rot90CW(vec1);
            return tmp.X * vec2.X + tmp.Y * vec2.Y < 0;
        }

        private static PointF[] RotatingCalipersMinAreaRect(PointF[] points)
        {
            int n = points.Length;
            float minarea = float.MaxValue;
            var invLen = new float[n];
            var vect = new PointF[n];
            int left = 0, bottom = 0, right = 0, top = 0;
            int[] seq = [-1, -1, -1, -1];
            var rot = new PointF[4];

            float orientation = 0;
            float base_a;
            float base_b = 0;

            // saved best: seq[3], base_a, width, base_b, height, seq[0]
            int bestLeft = 0, bestBottom = 0;
            float bestA = 0, bestW = 0, bestB = 0, bestH = 0;

            PointF pt0 = points[0];
            float left_x = pt0.X, right_x = pt0.X, top_y = pt0.Y, bottom_y = pt0.Y;

            for (int i = 0; i < n; i++)
            {
                if (pt0.X < left_x) { left_x = pt0.X; left = i; }
                if (pt0.X > right_x) { right_x = pt0.X; right = i; }
                if (pt0.Y > top_y) { top_y = pt0.Y; top = i; }
                if (pt0.Y < bottom_y) { bottom_y = pt0.Y; bottom = i; }

                PointF pt = points[i + 1 < n ? i + 1 : 0];
                double dx = pt.X - pt0.X;
                double dy = pt.Y - pt0.Y;
                vect[i].X = (float)dx;
                vect[i].Y = (float)dy;
                invLen[i] = (float)(1.0 / Math.Sqrt(dx * dx + dy * dy));
                pt0 = pt;
            }

            {
                double ax = vect[n - 1].X;
                double ay = vect[n - 1].Y;
                for (int i = 0; i < n; i++)
                {
                    double bx = vect[i].X;
                    double by = vect[i].Y;
                    double convexity = ax * by - ay * bx;
                    if (convexity != 0)
                    {
                        orientation = convexity > 0 ? 1f : -1f;
                        break;
                    }
                    ax = bx;
                    ay = by;
                }
                if (orientation == 0)
                    throw new InvalidOperationException("MinAreaRect: degenerate hull");
            }
            base_a = orientation;

            seq[0] = bottom;
            seq[1] = right;
            seq[2] = top;
            seq[3] = left;

            for (int k = 0; k < n; k++)
            {
                int main = 0;
                rot[0] = vect[seq[0]];
                rot[1] = Rot90CW(vect[seq[1]]);
                rot[2] = Rot180(vect[seq[2]]);
                rot[3] = Rot90CCW(vect[seq[3]]);
                for (int i = 1; i < 4; i++)
                    if (FirstVecIsRight(rot[i], rot[main]))
                        main = i;

                {
                    int p = seq[main];
                    float lead_x = vect[p].X * invLen[p];
                    float lead_y = vect[p].Y * invLen[p];
                    switch (main)
                    {
                        case 0: base_a = lead_x; base_b = lead_y; break;
                        case 1: base_a = lead_y; base_b = -lead_x; break;
                        case 2: base_a = -lead_x; base_b = -lead_y; break;
                        case 3: base_a = -lead_y; base_b = lead_x; break;
                    }
                }

                seq[main] += 1;
                seq[main] = seq[main] == n ? 0 : seq[main];

                {
                    float dx = points[seq[1]].X - points[seq[3]].X;
                    float dy = points[seq[1]].Y - points[seq[3]].Y;
                    float width = dx * base_a + dy * base_b;
                    dx = points[seq[2]].X - points[seq[0]].X;
                    dy = points[seq[2]].Y - points[seq[0]].Y;
                    float height = -dx * base_b + dy * base_a;
                    float area = width * height;
                    if (area <= minarea)
                    {
                        minarea = area;
                        bestLeft = seq[3];
                        bestA = base_a;
                        bestW = width;
                        bestB = base_b;
                        bestH = height;
                        bestBottom = seq[0];
                    }
                }
            }

            float A1 = bestA, B1 = bestB, A2 = -bestB, B2 = bestA;
            float C1 = A1 * points[bestLeft].X + points[bestLeft].Y * B1;
            float C2 = A2 * points[bestBottom].X + points[bestBottom].Y * B2;
            float idet = 1f / (A1 * B2 - A2 * B1);
            float px = (C1 * B2 - C2 * B1) * idet;
            float py = (A1 * C2 - A2 * C1) * idet;

            return
            [
                new PointF(px, py),
                new PointF(A1 * bestW, B1 * bestW),
                new PointF(A2 * bestH, B2 * bestH),
            ];
        }
    }
}
