using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace PictureSearch
{
    public class ImageAnalyzer
    {
        public string TargetClass { get; private set; }

        private class TemplateVariation
        {
            public Mat Descriptor { get; set; }
            public KeyPoint[] Keypoints { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private List<TemplateVariation> _variations = new List<TemplateVariation>();
        private double _magnification = 1.0;

        public void AnalyzeTemplate(Bitmap croppedTemplate)
        {
            if (croppedTemplate == null) return;

            _variations.Clear(); 
            TargetClass = "Пошук (ASIFT: 3D Ракурси та Віддзеркалення)...";

            _magnification = 1.0;
            int minSide = Math.Min(croppedTemplate.Width, croppedTemplate.Height);
            if (minSide < 100 && minSide > 0) _magnification = 100.0 / minSide;

            using var tempMat = BitmapConverter.ToMat(croppedTemplate);
            using var grayTemplate = new Mat();
            Cv2.CvtColor(tempMat, grayTemplate, ColorConversionCodes.BGR2GRAY);

            using var magnifiedTemplate = new Mat();
            Cv2.Resize(grayTemplate, magnifiedTemplate, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

            using var enhancedTemplate = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 3.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(magnifiedTemplate, enhancedTemplate);

            using var orb = ORB.Create(5000, 1.2f, 8, 15, 0, 2, 0, 15, 5);

            AddVariation(orb, enhancedTemplate);

            using var flippedTemplate = new Mat();
            Cv2.Flip(enhancedTemplate, flippedTemplate, FlipMode.Y);
            AddVariation(orb, flippedTemplate);

            using var warpedLeft = SimulatePerspective(enhancedTemplate, 0.7f, 1.0f);
            AddVariation(orb, warpedLeft);

            using var warpedRight = SimulatePerspective(enhancedTemplate, 1.0f, 0.7f);
            AddVariation(orb, warpedRight);
        }

        private void AddVariation(ORB orb, Mat image)
        {
            var descriptor = new Mat();
            orb.DetectAndCompute(image, null, out var keypoints, descriptor);

            if (descriptor.Rows > 0)
            {
                _variations.Add(new TemplateVariation
                {
                    Descriptor = descriptor,
                    Keypoints = keypoints,
                    Width = image.Width,
                    Height = image.Height
                });
            }
        }

        private Mat SimulatePerspective(Mat src, float leftScale, float rightScale)
        {
            var dst = new Mat();
            float w = src.Width;
            float h = src.Height;

            var srcPoints = new Point2f[] { new Point2f(0, 0), new Point2f(w, 0), new Point2f(0, h), new Point2f(w, h) };

            float leftY = h * (1f - leftScale) / 2f;
            float rightY = h * (1f - rightScale) / 2f;

            float newW = w * 0.75f;
            float offsetX = (w - newW) / 2f;

            var dstPoints = new Point2f[] {
                new Point2f(offsetX, leftY),                 // Верхній лівий
                new Point2f(offsetX + newW, rightY),         // Верхній правий
                new Point2f(offsetX, h - leftY),             // Нижній лівий
                new Point2f(offsetX + newW, h - rightY)      // Нижній правий
            };

            using var matrix = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
            Cv2.WarpPerspective(src, dst, matrix, src.Size(), InterpolationFlags.Cubic);
            return dst;
        }

        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            if (_variations.Count == 0) return matchedFiles;

            bool isSmallTemplate = _variations[0].Keypoints.Length < 150;

            int minRequiredInliers = isSmallTemplate ? 5 : 12;
            float ratioTestThreshold = isSmallTemplate ? 0.85f : 0.78f;
            double requiredInlierRatio = isSmallTemplate ? 0.03 : 0.08;

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));

            for (int i = 0; i < total; i++)
            {
                try
                {
                    using var targetImg = Cv2.ImRead(collectionPaths[i], ImreadModes.Grayscale);
                    if (targetImg.Empty()) continue;

                    using var magnifiedTarget = new Mat();
                    Cv2.Resize(targetImg, magnifiedTarget, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

                    using var enhancedTarget = new Mat();
                    clahe.Apply(magnifiedTarget, enhancedTarget);

                    using var orb = ORB.Create(20000, 1.2f, 8, 15, 0, 2, 0, 15, 7);
                    using var targetDescriptor = new Mat();
                    orb.DetectAndCompute(enhancedTarget, null, out var targetKeypoints, targetDescriptor);

                    if (targetDescriptor.Rows == 0) continue;

                    bool isMatchFound = false;

                    foreach (var variation in _variations)
                    {
                        var matches = matcher.KnnMatch(variation.Descriptor, targetDescriptor, k: 2);
                        var goodMatches = new List<DMatch>();

                        foreach (var m in matches)
                        {
                            if (m.Length > 1 && m[0].Distance < ratioTestThreshold * m[1].Distance)
                                goodMatches.Add(m[0]);
                        }

                        if (goodMatches.Count >= minRequiredInliers)
                        {
                            var srcPts = goodMatches.Select(m => variation.Keypoints[m.QueryIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();
                            var dstPts = goodMatches.Select(m => targetKeypoints[m.TrainIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();

                            using var mask = new Mat();
                            double ransacError = isSmallTemplate ? 5.0 : 4.0;
                            var homography = Cv2.FindHomography(InputArray.Create(srcPts), InputArray.Create(dstPts), HomographyMethods.Ransac, ransacError, mask);

                            if (!homography.Empty())
                            {
                                var objCorners = new Point2d[] {
                                    new Point2d(0, 0),
                                    new Point2d(variation.Width, 0),
                                    new Point2d(variation.Width, variation.Height),
                                    new Point2d(0, variation.Height)
                                };

                                var sceneCorners = Cv2.PerspectiveTransform(objCorners, homography);
                                var pointsForConvex = sceneCorners.Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToList();

                                bool isConvex = Cv2.IsContourConvex(pointsForConvex);
                                bool geometryPassed = isSmallTemplate ? true : isConvex;

                                double area = Cv2.ContourArea(pointsForConvex);
                                bool isAreaValid = area > (variation.Width * variation.Height * 0.02);

                                if (geometryPassed && isAreaValid)
                                {
                                    int inliersCount = 0;
                                    byte[] maskBytes = new byte[mask.Rows * mask.Cols];
                                    System.Runtime.InteropServices.Marshal.Copy(mask.Data, maskBytes, 0, maskBytes.Length);

                                    foreach (var b in maskBytes)
                                    {
                                        if (b > 0) inliersCount++;
                                    }

                                    if (inliersCount >= minRequiredInliers)
                                    {
                                        double inlierRatio = (double)inliersCount / goodMatches.Count;
                                        if (inlierRatio >= requiredInlierRatio)
                                        {
                                            isMatchFound = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (isMatchFound) matchedFiles.Add(collectionPaths[i]);
                }
                catch (Exception) { }

                reportProgress(i + 1, total);
            }
            return matchedFiles;
        }
    }
}