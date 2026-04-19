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

        public void AnalyzeTemplate(Bitmap croppedTemplate)
        {
            TargetClass = "Пошук об'єктів (ORB + CLAHE)...";

            using var tempMat = BitmapConverter.ToMat(croppedTemplate);
            using var grayTemplate = new Mat();
            Cv2.CvtColor(tempMat, grayTemplate, ColorConversionCodes.BGR2GRAY);

            // ПОКРАЩЕННЯ 1: Підвищуємо контраст, щоб знаходити точки на гладких поверхнях
            using var enhancedTemplate = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(grayTemplate, enhancedTemplate);

            // Робимо алгоритм гіперчутливим для гладких поверхонь та маленьких фрагментів
            using var orb = ORB.Create(
                5000,   // 1. Кількість точок
                1.2f,   // 2. ScaleFactor 
                8,      // 3. NLevels 
                15,     // 4. EdgeThreshold (пускаємо ближче до країв)
                0,      // 5. FirstLevel 
                2,      // 6. WTA_K 
                0,      // 7. ScoreType (Магія C#! Передаємо 0 замість назви)
                15,     // 8. PatchSize (зменшуємо візерунок)
                10       // 9. FastThreshold (ГОЛОВНЕ: гіперчутливість до дрібних тіней на мордочці)
            );
            _templateDescriptor = new Mat();

            orb.DetectAndCompute(enhancedTemplate, null, out _templateKeypoints, _templateDescriptor);
        }

        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            // Створюємо CLAHE один раз для всіх фото колекції
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));

            for (int i = 0; i < total; i++)
            {
                try
                {
                    using var targetImg = Cv2.ImRead(collectionPaths[i], ImreadModes.Grayscale);
                    if (targetImg.Empty()) continue;

                    // Застосовуємо такий самий контраст до фото з колекції
                    using var enhancedTarget = new Mat();
                    clahe.Apply(targetImg, enhancedTarget);

                    using var orb = ORB.Create(
                        5000,   // 1. Кількість точок
                        1.2f,   // 2. ScaleFactor 
                        8,      // 3. NLevels 
                        15,     // 4. EdgeThreshold (пускаємо ближче до країв)
                        0,      // 5. FirstLevel 
                        2,      // 6. WTA_K 
                        0,      // 7. ScoreType (Магія C#! Передаємо 0 замість назви)
                        15,     // 8. PatchSize (зменшуємо візерунок)
                        10       // 9. FastThreshold (ГОЛОВНЕ: гіперчутливість до дрібних тіней на мордочці)
                    );
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

                    // Зменшили вимогу до початкових точок, щоб ловити менші деталі
                    // Вимагаємо мінімум 15 хороших точок для старту перевірки (було 8)
                    if (goodMatches.Count >= 15)
                    {
                        var srcPts = goodMatches.Select(m => _templateKeypoints[m.QueryIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();
                        var dstPts = goodMatches.Select(m => targetKeypoints[m.TrainIdx].Pt).Select(p => new Point2d(p.X, p.Y)).ToArray();

                        using var mask = new Mat();
                        // Зменшуємо похибку RANSAC з 5.0 до 3.0. Точки мають лежати майже ідеально!
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

                            // Якщо маємо 15 ІДЕАЛЬНИХ збігів контурів - це точно наш об'єкт (було 8)
                            if (inliersCount >= 15)
                            {
                                matchedFiles.Add(collectionPaths[i]);
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