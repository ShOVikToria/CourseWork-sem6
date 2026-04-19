using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using OpenCvSharp.Features2D;

namespace PictureSearch
{
    public class ImageAnalyzer
    {
        public string TargetClass { get; private set; }

        private Mat _templateDescriptor;
        private KeyPoint[] _templateKeypoints;
        private double _magnification = 1.0;
        private int _templateWidth;
        private int _templateHeight;

        public void AnalyzeTemplate(Bitmap croppedTemplate)
        {
            if (croppedTemplate == null) return;

            TargetClass = "Пошук (Максимальна точність ORB)...";

            _magnification = 1.0;
            int minSide = Math.Min(croppedTemplate.Width, croppedTemplate.Height);

            if (minSide < 100 && minSide > 0)
            {
                _magnification = 100.0 / minSide;
            }

            using var tempMat = BitmapConverter.ToMat(croppedTemplate);
            using var grayTemplate = new Mat();
            Cv2.CvtColor(tempMat, grayTemplate, ColorConversionCodes.BGR2GRAY);

            using var magnifiedTemplate = new Mat();
            Cv2.Resize(grayTemplate, magnifiedTemplate, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

            _templateWidth = magnifiedTemplate.Width;
            _templateHeight = magnifiedTemplate.Height;

            using var enhancedTemplate = new Mat();
            // ЗМІНЕНО: Підвищили контрастність еталону (clipLimit з 2.0 до 3.0)
            using var clahe = Cv2.CreateCLAHE(clipLimit: 3.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(magnifiedTemplate, enhancedTemplate);

            // ЗМІНЕНО: Робимо еталон гіперчутливим до найменших тіней (fastThreshold: 5)
            using var orb = ORB.Create(5000, 1.2f, 8, 15, 0, 2, 0, 15, 5);
            _templateDescriptor = new Mat();
            orb.DetectAndCompute(enhancedTemplate, null, out _templateKeypoints, _templateDescriptor);
        }

        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            if (_templateKeypoints == null || _templateKeypoints.Length < 4)
                return matchedFiles;

            bool isSmallTemplate = _templateKeypoints.Length < 150;

            // ЗМІНЕНО: Дозволяємо ловити об'єкт навіть за 5 хорошими точками
            int minRequiredInliers = isSmallTemplate ? 5 : 15;
            float ratioTestThreshold = isSmallTemplate ? 0.85f : 0.75f;
            double requiredInlierRatio = isSmallTemplate ? 0.03 : 0.12;

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

                    // ЗМІНЕНО: Даємо колекції ліміт у 20 000 точок, щоб дрібні деталі не губилися!
                    using var orb = ORB.Create(20000, 1.2f, 8, 15, 0, 2, 0, 15, 7);
                    using var targetDescriptor = new Mat();
                    orb.DetectAndCompute(enhancedTarget, null, out var targetKeypoints, targetDescriptor);

                    if (targetDescriptor.Rows == 0 || _templateDescriptor.Rows == 0) continue;

                    var matches = matcher.KnnMatch(_templateDescriptor, targetDescriptor, k: 2);
                    var goodMatches = new List<DMatch>();

                    foreach (var m in matches)
                    {
                        if (m.Length > 1 && m[0].Distance < ratioTestThreshold * m[1].Distance)
                            goodMatches.Add(m[0]);
                    }

                    if (goodMatches.Count >= minRequiredInliers)
                    {
                        var srcPts = goodMatches.Select(m => _templateKeypoints[m.QueryIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();
                        var dstPts = goodMatches.Select(m => targetKeypoints[m.TrainIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();

                        using var mask = new Mat();
                        // ЗМІНЕНО: Послаблюємо RANSAC для малих об'єктів (5.0 замість 3.0), бо малі деталі можуть трохи "з'їжджати"
                        double ransacError = isSmallTemplate ? 5.0 : 3.0;
                        var homography = Cv2.FindHomography(InputArray.Create(srcPts), InputArray.Create(dstPts), HomographyMethods.Ransac, ransacError, mask);

                        if (!homography.Empty())
                        {
                            var objCorners = new Point2d[] {
                                new Point2d(0, 0),
                                new Point2d(_templateWidth, 0),
                                new Point2d(_templateWidth, _templateHeight),
                                new Point2d(0, _templateHeight)
                            };

                            var sceneCorners = Cv2.PerspectiveTransform(objCorners, homography);
                            var pointsForConvex = sceneCorners.Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToList();

                            // ЗМІНЕНО: Геометрична перевірка тепер гнучка!
                            // Для малих ділянок ми вимикаємо перевірку на "перекручування", бо матриця перспективи на 5 точках завжди трохи ламається
                            bool isConvex = Cv2.IsContourConvex(pointsForConvex);
                            bool geometryPassed = isSmallTemplate ? true : isConvex;

                            // Площа все одно не повинна бути мікроскопічною (мінімум 2% від еталону)
                            double area = Cv2.ContourArea(pointsForConvex);
                            bool isAreaValid = area > (_templateWidth * _templateHeight * 0.02);

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
                                        matchedFiles.Add(collectionPaths[i]);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }

                reportProgress(i + 1, total);
            }
            return matchedFiles;
        }
    }
}