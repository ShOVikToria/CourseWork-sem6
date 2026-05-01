using System.Collections.Concurrent;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Threading;
using System.Threading.Tasks;

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
            if (minSide < 100 && minSide > 0)
            {
                _magnification = 100.0 / minSide;
            }

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

            float[] perspectiveScales = { 0.85f, 0.70f, 0.55f };

            foreach (float scale in perspectiveScales)
            {
                using var warpedLeft = SimulatePerspective(enhancedTemplate, scale, 1.0f);
                AddVariation(orb, warpedLeft);

                using var warpedRight = SimulatePerspective(enhancedTemplate, 1.0f, scale);
                AddVariation(orb, warpedRight);
            }
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

            var srcPoints = new Point2f[] {
                new Point2f(0, 0),
                new Point2f(w, 0),
                new Point2f(0, h),
                new Point2f(w, h)
            };

            float leftY = h * (1f - leftScale) / 2f;
            float rightY = h * (1f - rightScale) / 2f;

            float newW = w * Math.Min(leftScale, rightScale);
            float offsetX = (w - newW) / 2f;

            var dstPoints = new Point2f[] {
                new Point2f(offsetX, leftY),                 
                new Point2f(offsetX + newW, rightY),      
                new Point2f(offsetX, h - leftY),            
                new Point2f(offsetX + newW, h - rightY)     
            };

            using var matrix = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
            Cv2.WarpPerspective(src, dst, matrix, src.Size(), InterpolationFlags.Cubic);

            return dst;
        }

        private bool CheckIfMatchFound(Mat targetDescriptor, KeyPoint[] targetKeypoints, BFMatcher matcher, (int MinInliers, float RatioThreshold, double InlierRatio) config)
        {
            foreach (var variation in _variations)
            {
                var matches = matcher.KnnMatch(variation.Descriptor, targetDescriptor, k: 2);
                var goodMatches = new List<DMatch>();

                foreach (var m in matches)
                {
                    if (m.Length > 1 && m[0].Distance < config.RatioThreshold * m[1].Distance)
                        goodMatches.Add(m[0]);
                }

                if (goodMatches.Count >= config.MinInliers)
                {
                    var srcPts = new Point2d[goodMatches.Count];
                    var dstPts = new Point2d[goodMatches.Count];
                    for (int j = 0; j < goodMatches.Count; j++)
                    {
                        var sp = variation.Keypoints[goodMatches[j].QueryIdx].Pt;
                        var dp = targetKeypoints[goodMatches[j].TrainIdx].Pt;
                        srcPts[j] = new Point2d(sp.X, sp.Y);
                        dstPts[j] = new Point2d(dp.X, dp.Y);
                    }

                    using var mask = new Mat();
                    var homography = Cv2.FindHomography(InputArray.Create(srcPts), InputArray.Create(dstPts), HomographyMethods.Ransac, 3.0, mask);

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
                        double area = Cv2.ContourArea(pointsForConvex);
                        bool isAreaValid = area > (variation.Width * variation.Height * 0.02);

                        double top = Math.Sqrt(Math.Pow(sceneCorners[1].X - sceneCorners[0].X, 2) + Math.Pow(sceneCorners[1].Y - sceneCorners[0].Y, 2));
                        double bottom = Math.Sqrt(Math.Pow(sceneCorners[3].X - sceneCorners[2].X, 2) + Math.Pow(sceneCorners[3].Y - sceneCorners[2].Y, 2));
                        double left = Math.Sqrt(Math.Pow(sceneCorners[3].X - sceneCorners[0].X, 2) + Math.Pow(sceneCorners[3].Y - sceneCorners[0].Y, 2));
                        double right = Math.Sqrt(Math.Pow(sceneCorners[2].X - sceneCorners[1].X, 2) + Math.Pow(sceneCorners[2].Y - sceneCorners[1].Y, 2));

                        bool isReasonablePerspective =
                            (top / bottom > 0.3 && top / bottom < 3.0) &&
                            (left / right > 0.3 && left / right < 3.0);

                        if (isConvex && isAreaValid && isReasonablePerspective)
                        {
                            int inliersCount = 0;
                            byte[] maskBytes = new byte[mask.Rows * mask.Cols];
                            System.Runtime.InteropServices.Marshal.Copy(mask.Data, maskBytes, 0, maskBytes.Length);

                            foreach (var b in maskBytes)
                            {
                                if (b > 0) inliersCount++;
                            }

                            if (inliersCount >= config.MinInliers)
                            {
                                double inlierRatio = (double)inliersCount / goodMatches.Count;
                                if (inlierRatio >= config.InlierRatio)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        private void PreprocessImage(Mat sourceImg, Mat magTarget, Mat enhTarget, Mat targetDesc, CLAHE clahe, ORB orb, out KeyPoint[] keypoints)
        {
            Cv2.Resize(sourceImg, magTarget, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);
            clahe.Apply(magTarget, enhTarget);
            orb.DetectAndCompute(enhTarget, null, out keypoints, targetDesc);
        }

        private (int MinInliers, float RatioThreshold, double InlierRatio) GetMatchConfig()
        {
            bool isSmallTemplate = _variations[0].Keypoints.Length < 150;
            return (
                MinInliers: isSmallTemplate ? 10 : 15,
                RatioThreshold: 0.75f,
                InlierRatio: isSmallTemplate ? 0.08 : 0.15
            );
        }

        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            if (_variations.Count == 0 || _variations[0].Keypoints.Length < 20)
                return matchedFiles;

            var config = GetMatchConfig();

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            using var orb = ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7);

            using var magnifiedTarget = new Mat();
            using var enhancedTarget = new Mat();
            using var targetDescriptor = new Mat();

            for (int i = 0; i < total; i++)
            {
                try
                {
                    using var targetImg = Cv2.ImRead(collectionPaths[i], ImreadModes.Grayscale);
                    if (targetImg.Empty()) continue;

                    PreprocessImage(targetImg, magnifiedTarget, enhancedTarget, targetDescriptor, clahe, orb, out var targetKeypoints);

                    if (targetDescriptor.Rows < config.MinInliers) continue;

                    bool isMatchFound = CheckIfMatchFound(targetDescriptor, targetKeypoints, matcher, config);

                    if (isMatchFound) matchedFiles.Add(collectionPaths[i]);
                }
                catch (Exception) { }

                reportProgress(i + 1, total);
            }
            return matchedFiles;
        }
        public List<string> SearchParallel(List<string> collectionPaths, Action<int, int> reportProgress, int threadCount = -1)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            if (_variations.Count == 0 || _variations[0].Keypoints.Length < 20)
                return matchedFiles;

            Cv2.SetNumThreads(1);

            var config = GetMatchConfig();

            var matchedBag = new ConcurrentBag<string>();
            int processedCount = 0;

            int degreeOfParallelism = (threadCount > 0) ? threadCount : Environment.ProcessorCount;
            int chunkSize = (total + degreeOfParallelism - 1) / degreeOfParallelism;

            Parallel.ForEach(
                Partitioner.Create(0, total, chunkSize),
                new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },

                () => new
                {
                    matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false),
                    clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8)),
                    orb = ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7),
                    magTarget = new Mat(),
                    enhTarget = new Mat(),
                    targetDesc = new Mat()
                },

                (range, loopState, localState) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        var path = collectionPaths[i];

                        try
                        {
                            using var targetImg = Cv2.ImRead(path, ImreadModes.Grayscale);

                            if (targetImg.Empty()) continue;

                            PreprocessImage(targetImg, localState.magTarget, localState.enhTarget, localState.targetDesc, localState.clahe, localState.orb, out var targetKeypoints);

                            if (localState.targetDesc.Rows < config.MinInliers) continue;

                            bool isMatchFound = CheckIfMatchFound(localState.targetDesc, targetKeypoints, localState.matcher, config);

                            if (isMatchFound) matchedBag.Add(path);
                        }
                        catch (Exception) { }

                        int current = Interlocked.Increment(ref processedCount);
                        if (current % 20 == 0 || current == total)
                        {
                            reportProgress(current, total);
                        }
                    }
                    return localState;
                },

                localState =>
                {
                    localState.matcher.Dispose();
                    localState.clahe.Dispose();
                    localState.orb.Dispose();
                    localState.magTarget.Dispose();
                    localState.enhTarget.Dispose();
                    localState.targetDesc.Dispose();
                }
            );
            Cv2.SetNumThreads(-1);

            var pathIndexMap = collectionPaths
                .Select((path, index) => (path, index))
                .ToDictionary(x => x.path, x => x.index);

            matchedFiles = matchedBag
                .OrderBy(path => pathIndexMap[path])
                .ToList();

            return matchedFiles;
        }

        // ПОСЛІДОВНИЙ АЛГОРИТМ ДЛЯ ЕКСПЕРИМЕНТУ (без читання з диска)
        public void SearchSequentialBenchmark(List<Mat> preloadedImages, Action<int, int> reportProgress)
        {
            int total = preloadedImages.Count;
            var config = GetMatchConfig();

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            using var orb = ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7);

            using var magnifiedTarget = new Mat();
            using var enhancedTarget = new Mat();
            using var targetDescriptor = new Mat();

            for (int i = 0; i < total; i++)
            {
                Mat targetImg = preloadedImages[i]; // Беремо вже готову картинку з пам'яті
                if (targetImg.Empty()) continue;

                PreprocessImage(targetImg, magnifiedTarget, enhancedTarget, targetDescriptor, clahe, orb, out var targetKeypoints);

                if (targetDescriptor.Rows < config.MinInliers) continue;

                CheckIfMatchFound(targetDescriptor, targetKeypoints, matcher, config);

                reportProgress?.Invoke(i + 1, total);
            }
        }

        // ПАРАЛЕЛЬНИЙ АЛГОРИТМ ДЛЯ ЕКСПЕРИМЕНТУ (без читання з диска)
        public void SearchParallelBenchmark(List<Mat> preloadedImages, Action<int, int> reportProgress, int threadCount)
        {
            int total = preloadedImages.Count;
            Cv2.SetNumThreads(1);
            var config = GetMatchConfig();
            int processedCount = 0;

            int degreeOfParallelism = (threadCount > 0) ? threadCount : Environment.ProcessorCount;
            int chunkSize = (total + degreeOfParallelism - 1) / degreeOfParallelism;

            Parallel.ForEach(
                Partitioner.Create(0, total, chunkSize),
                new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
                () => new {
                    matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false),
                    clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8)),
                    orb = ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7),
                    magTarget = new Mat(),
                    enhTarget = new Mat(),
                    targetDesc = new Mat()
                },
                (range, loopState, localState) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        Mat targetImg = preloadedImages[i]; // Беремо вже готову картинку з пам'яті
                        if (targetImg.Empty()) continue;

                        PreprocessImage(targetImg, localState.magTarget, localState.enhTarget, localState.targetDesc, localState.clahe, localState.orb, out var targetKeypoints);

                        if (localState.targetDesc.Rows < config.MinInliers) continue;

                        CheckIfMatchFound(localState.targetDesc, targetKeypoints, localState.matcher, config);

                        int current = Interlocked.Increment(ref processedCount);
                        if (current % 20 == 0 || current == total)
                            reportProgress?.Invoke(current, total);
                    }
                    return localState;
                },
                localState => {
                    localState.matcher.Dispose(); localState.clahe.Dispose(); localState.orb.Dispose();
                    localState.magTarget.Dispose(); localState.enhTarget.Dispose(); localState.targetDesc.Dispose();
                }
            );
            Cv2.SetNumThreads(-1);
        }

    }
}