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
        private double _magnification = 1.0; // Змінна для зберігання масштабу "лупи"

        public void AnalyzeTemplate(Bitmap croppedTemplate)
        {
            TargetClass = "Пошук об'єктів (ORB + CLAHE + Magnifier)...";

            // ЦИФРОВА ЛУПА: Якщо картинка занадто мала, вираховуємо коефіцієнт збільшення
            _magnification = 1.0;
            int minSide = Math.Min(croppedTemplate.Width, croppedTemplate.Height);
            if (minSide < 100 && minSide > 0)
            {
                _magnification = 100.0 / minSide; // Наприклад, 40px збільшиться у 2.5 рази
            }

            using var tempMat = BitmapConverter.ToMat(croppedTemplate);
            using var grayTemplate = new Mat();
            Cv2.CvtColor(tempMat, grayTemplate, ColorConversionCodes.BGR2GRAY);

            // Застосовуємо збільшення до еталону
            using var magnifiedTemplate = new Mat();
            Cv2.Resize(grayTemplate, magnifiedTemplate, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

            using var enhancedTemplate = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(magnifiedTemplate, enhancedTemplate);

            using var orb = ORB.Create(5000, 1.2f, 8, 15, 0, 2, 0, 15, 10);
            _templateDescriptor = new Mat();
            orb.DetectAndCompute(enhancedTemplate, null, out _templateKeypoints, _templateDescriptor);
        }

        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));

            for (int i = 0; i < total; i++)
            {
                try
                {
                    using var targetImg = Cv2.ImRead(collectionPaths[i], ImreadModes.Grayscale);
                    if (targetImg.Empty()) continue;

                    // Застосовуємо ТАКЕ САМЕ збільшення до фото з колекції
                    using var magnifiedTarget = new Mat();
                    Cv2.Resize(targetImg, magnifiedTarget, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

                    using var enhancedTarget = new Mat();
                    clahe.Apply(magnifiedTarget, enhancedTarget);

                    using var orb = ORB.Create(5000, 1.2f, 8, 15, 0, 2, 0, 15, 10);
                    using var targetDescriptor = new Mat();
                    orb.DetectAndCompute(enhancedTarget, null, out var targetKeypoints, targetDescriptor);

                    if (targetDescriptor.Rows == 0 || _templateDescriptor.Rows == 0) continue;

                    var matches = matcher.KnnMatch(_templateDescriptor, targetDescriptor, k: 2);
                    var goodMatches = new List<DMatch>();
                    foreach (var m in matches)
                    {
                        if (m.Length > 1 && m[0].Distance < 0.75f * m[1].Distance)
                        {
                            goodMatches.Add(m[0]);
                        }
                    }

                    if (goodMatches.Count >= 15)
                    {
                        var srcPts = goodMatches.Select(m => _templateKeypoints[m.QueryIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();
                        var dstPts = goodMatches.Select(m => targetKeypoints[m.TrainIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();

                        using var mask = new Mat();
                        var homography = Cv2.FindHomography(InputArray.Create(srcPts), InputArray.Create(dstPts), HomographyMethods.Ransac, 3.0, mask);

                        if (!homography.Empty())
                        {
                            int inliersCount = 0;
                            byte[] maskBytes = new byte[mask.Rows * mask.Cols];
                            System.Runtime.InteropServices.Marshal.Copy(mask.Data, maskBytes, 0, maskBytes.Length);

                            foreach (var b in maskBytes)
                            {
                                if (b > 0) inliersCount++;
                            }

                            // НОВЕ: Рахуємо "Відсоток достовірності" (Inlier Ratio)
                            // Ділимо кількість ідеальних точок на кількість усіх хороших збігів
                            double inlierRatio = (double)inliersCount / goodMatches.Count;

                            // Якщо маємо мінімум 15 ІДЕАЛЬНИХ збігів...
                            if (inliersCount >= 15)
                            {
                                // ...І ці збіги становлять хоча б 12% від усіх знайдених (відсіюємо море і хаос)
                                if (inlierRatio >= 0.12)
                                {
                                    matchedFiles.Add(collectionPaths[i]);
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