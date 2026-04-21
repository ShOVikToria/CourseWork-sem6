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

        /// <summary>
        /// Попередня обробка та аналіз еталонного зображення. 
        /// Генерація дескрипторів для оригіналу та його просторових варіацій 
        /// (віддзеркалення, нахили) для забезпечення інваріантності до ракурсу.
        /// </summary>
        /// <param name="croppedTemplate">Вирізаний фрагмент зображення для пошуку.</param>
        public void AnalyzeTemplate(Bitmap croppedTemplate)
        {
            // Захист від передачі порожнього об'єкта
            if (croppedTemplate == null) return;

            // Очищення пулу варіацій перед початком нового аналізу
            _variations.Clear();
            TargetClass = "Пошук (ASIFT: 3D Ракурси та Віддзеркалення)...";

            // Адаптивне масштабування для запобігання нестачі ключових точок на малих зображеннях.
            // Цільовий мінімальний розмір сторони - 100 пікселів.
            _magnification = 1.0;
            int minSide = Math.Min(croppedTemplate.Width, croppedTemplate.Height);
            if (minSide < 100 && minSide > 0)
            {
                _magnification = 100.0 / minSide;
            }

            // Конвертація зображення у внутрішній формат OpenCV та переведення в градації сірого
            using var tempMat = BitmapConverter.ToMat(croppedTemplate);
            using var grayTemplate = new Mat();
            Cv2.CvtColor(tempMat, grayTemplate, ColorConversionCodes.BGR2GRAY);

            // Масштабування зображення з використанням бікубічної інтерполяції
            using var magnifiedTemplate = new Mat();
            Cv2.Resize(grayTemplate, magnifiedTemplate, new OpenCvSharp.Size(0, 0), _magnification, _magnification, InterpolationFlags.Cubic);

            // Фотометричне вирівнювання: застосування CLAHE для компенсації перепадів освітлення та виділення тіней
            using var enhancedTemplate = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 3.0, tileGridSize: new OpenCvSharp.Size(8, 8));
            clahe.Apply(magnifiedTemplate, enhancedTemplate);

            // Ініціалізація екстрактора ORB.
            // Встановлено низький поріг fastThreshold (5) для виявлення слабких кутів на дрібних деталях.
            using var orb = ORB.Create(5000, 1.2f, 8, 15, 0, 2, 0, 15, 5);

            // Генерація просторових варіацій еталона (імітація ASIFT)

            // 1. Базовий оригінал
            AddVariation(orb, enhancedTemplate);

            // 2. Горизонтальне віддзеркалення (інваріантність до повороту на 180 градусів по осі Y)
            using var flippedTemplate = new Mat();
            Cv2.Flip(enhancedTemplate, flippedTemplate, FlipMode.Y);
            AddVariation(orb, flippedTemplate);

            // Генерація серії просторових варіацій (Повний ASIFT)
            // Використовуються коефіцієнти стиснення ширини: 0.85 (15%), 0.70 (30%), 0.55 (45%)
            float[] perspectiveScales = { 0.85f, 0.70f, 0.55f };

            foreach (float scale in perspectiveScales)
            {
                // Імітація повороту вліво (практично симулює кути огляду ~31°, 45°, 56°)
                using var warpedLeft = SimulatePerspective(enhancedTemplate, scale, 1.0f);
                AddVariation(orb, warpedLeft);

                // Імітація повороту вправо
                using var warpedRight = SimulatePerspective(enhancedTemplate, 1.0f, scale);
                AddVariation(orb, warpedRight);
            }
        }
        /// <summary>
        /// Виконує вилучення ключових точок та обчислення дескрипторів для переданого зображення.
        /// У разі успіху зберігає результати до загального пулу варіацій еталона.
        /// </summary>
        /// <param name="orb">Ініціалізований екземпляр детектора ORB.</param>
        /// <param name="image">Матриця зображення (варіація еталона) для аналізу.</param>
        private void AddVariation(ORB orb, Mat image)
        {
            var descriptor = new Mat();

            // Виявлення просторових особливостей (ключових точок) та генерація їхнього бінарного опису
            orb.DetectAndCompute(image, null, out var keypoints, descriptor);

            // Додавання варіації до колекції виконується лише за умови успішного знаходження ознак
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

        /// <summary>
        /// Створює штучне перспективне спотворення зображення для імітації 3D-повороту (out-of-plane rotation).
        /// Використовується для розширення інваріантності алгоритму до зміни ракурсу.
        /// </summary>
        /// <param name="src">Вихідна матриця зображення.</param>
        /// <param name="leftScale">Коефіцієнт вертикального масштабування лівого краю (1.0 - без змін).</param>
        /// <param name="rightScale">Коефіцієнт вертикального масштабування правого краю (1.0 - без змін).</param>
        /// <returns>Нова матриця зображення із застосованою геометричною трансформацією.</returns>
        private Mat SimulatePerspective(Mat src, float leftScale, float rightScale)
        {
            var dst = new Mat();
            float w = src.Width;
            float h = src.Height;

            // Визначення координат чотирьох кутів оригінального прямокутного зображення
            var srcPoints = new Point2f[] {
                new Point2f(0, 0),
                new Point2f(w, 0),
                new Point2f(0, h),
                new Point2f(w, h)
            };

            // Обчислення вертикальних відступів (Y-координат) для імітації віддалення відповідного краю
            float leftY = h * (1f - leftScale) / 2f;
            float rightY = h * (1f - rightScale) / 2f;

            // Горизонтальне стиснення зображення на 25% для імітації візуального скорочення об'єкта при повороті
            float newW = w * Math.Min(leftScale, rightScale);
            float offsetX = (w - newW) / 2f;

            // Формування координат цільового полігона (трапеції) після трансформації
            var dstPoints = new Point2f[] {
                new Point2f(offsetX, leftY),                 // Верхній лівий
                new Point2f(offsetX + newW, rightY),         // Верхній правий
                new Point2f(offsetX, h - leftY),             // Нижній лівий
                new Point2f(offsetX + newW, h - rightY)      // Нижній правий
            };

            // Обчислення матриці перспективного перетворення (гомографії) 
            // та її застосування до зображення з використанням бікубічної інтерполяції
            using var matrix = Cv2.GetPerspectiveTransform(srcPoints, dstPoints);
            Cv2.WarpPerspective(src, dst, matrix, src.Size(), InterpolationFlags.Cubic);

            return dst;
        }

        /// <summary>
        /// Виконує послідовний пошук еталона у заданій колекції зображень.
        /// Використовує зіставлення локальних ознак (ORB) із подальшою перевіркою 
        /// геометричної узгодженості (гомографія + RANSAC).
        /// </summary>
        /// <param name="collectionPaths">Список шляхів до зображень, серед яких виконується пошук.</param>
        /// <param name="reportProgress">Функція зворотного виклику для оновлення статусу виконання (UI).</param>
        /// <returns>Список шляхів до файлів, у яких знайдено збіг з еталоном.</returns>
        public List<string> SearchSequential(List<string> collectionPaths, Action<int, int> reportProgress)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            // Захист від "порожніх" ділянок (неба, гладкого листя, стін)
            // Якщо на оригіналі менше 20 точок - це текстурний шум, шукати його немає сенсу
            if (_variations.Count == 0 || _variations[0].Keypoints.Length < 20)
                return matchedFiles;

            // Оцінка інформаційної щільності еталона для застосування адаптивних порогів
            bool isSmallTemplate = _variations[0].Keypoints.Length < 150;

            // Налаштування динамічних порогів фільтрації:
            // 1. Мінімальна кількість точок, що підтверджують геометричну модель (inliers)
            int minRequiredInliers = isSmallTemplate ? 10 : 15;
            // 2. Поріг для тесту Лоу (Lowe's Ratio Test) для відсіювання неоднозначних збігів
            float ratioTestThreshold = 0.75f;
            // 3. Мінімально допустима частка inliers серед усіх потенційних збігів (фільтр фонового шуму)
            double requiredInlierRatio = isSmallTemplate ? 0.08 : 0.15;

            // Ініціалізація інструментів порівняння (Hamming відстань для бінарних дескрипторів ORB)
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

                    // ЗМІНЕНО: Зменшуємо ліміт точок до 8000, щоб море не мало шансів генерувати математичний хаос
                    using var orb = ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7);
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

                                // 1. Тест на опуклість (чи не перекручена рамка вісімкою)
                                bool isConvex = Cv2.IsContourConvex(pointsForConvex);

                                // 2. Тест на площу (мінімум 2% від еталона)
                                double area = Cv2.ContourArea(pointsForConvex);
                                bool isAreaValid = area > (variation.Width * variation.Height * 0.02);

                                // --- НОВЕ: Тест на адекватність пропорцій (Захист від моря) ---
                                // Вираховуємо довжину всіх чотирьох сторін нашої знайденої рамки
                                double top = Math.Sqrt(Math.Pow(sceneCorners[1].X - sceneCorners[0].X, 2) + Math.Pow(sceneCorners[1].Y - sceneCorners[0].Y, 2));
                                double bottom = Math.Sqrt(Math.Pow(sceneCorners[3].X - sceneCorners[2].X, 2) + Math.Pow(sceneCorners[3].Y - sceneCorners[2].Y, 2));
                                double left = Math.Sqrt(Math.Pow(sceneCorners[3].X - sceneCorners[0].X, 2) + Math.Pow(sceneCorners[3].Y - sceneCorners[0].Y, 2));
                                double right = Math.Sqrt(Math.Pow(sceneCorners[2].X - sceneCorners[1].X, 2) + Math.Pow(sceneCorners[2].Y - sceneCorners[1].Y, 2));

                                // Рамка не повинна бути екстремально витягнутою (відношення протилежних сторін має бути в межах 0.3 - 3.0)
                                // Якщо верхня сторона в 5 разів довша за нижню - це не перспектива, це математична помилка (море!)
                                bool isReasonablePerspective =
                                    (top / bottom > 0.3 && top / bottom < 3.0) &&
                                    (left / right > 0.3 && left / right < 3.0);

                                // Перевіряємо всі ТРИ умови одночасно
                                if (isConvex && isAreaValid && isReasonablePerspective)
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

        public List<string> SearchParallel(List<string> collectionPaths, Action<int, int> reportProgress, int threadCount = -1)
        {
            List<string> matchedFiles = new List<string>();
            int total = collectionPaths.Count;

            if (_variations.Count == 0 || _variations[0].Keypoints.Length < 20)
                return matchedFiles;

            bool isSmallTemplate = _variations[0].Keypoints.Length < 150;
            int minRequiredInliers = isSmallTemplate ? 10 : 15;
            float ratioTestThreshold = 0.75f;
            double requiredInlierRatio = isSmallTemplate ? 0.08 : 0.15;

            var matchedBag = new ConcurrentBag<string>();
            int processedCount = 0;

            // Якщо threadCount <= 0 або не передано — використовуємо всі доступні потоки
            int degreeOfParallelism = (threadCount > 0)
                ? threadCount
                : Environment.ProcessorCount;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = degreeOfParallelism
            };

            Parallel.ForEach(
                collectionPaths,
                parallelOptions,
                // ORB тепер створюється ОДИН РАЗ на потік, а не на кожне зображення
                () => (
                    matcher: new BFMatcher(NormTypes.Hamming, crossCheck: false),
                    clahe: Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8)),
                    orb: ORB.Create(8000, 1.2f, 8, 15, 0, 2, 0, 15, 7)
                ),
                (path, loopState, localState) =>
                {
                    try
                    {
                        using var targetImg = Cv2.ImRead(path, ImreadModes.Grayscale);
                        if (targetImg.Empty()) return localState;

                        using var magnifiedTarget = new Mat();
                        Cv2.Resize(targetImg, magnifiedTarget, new OpenCvSharp.Size(0, 0),
                            _magnification, _magnification, InterpolationFlags.Cubic);

                        using var enhancedTarget = new Mat();
                        localState.clahe.Apply(magnifiedTarget, enhancedTarget);

                        // БІЛЬШЕ НЕ СТВОРЮЄМО ORB ТУТ
                        using var targetDescriptor = new Mat();
                        localState.orb.DetectAndCompute(enhancedTarget, null, out var targetKeypoints, targetDescriptor);

                if (targetDescriptor.Rows == 0) return localState;

                        bool isMatchFound = false;

                        foreach (var variation in _variations)
                        {
                            var matches = localState.matcher.KnnMatch(variation.Descriptor, targetDescriptor, k: 2);
                            var goodMatches = new List<DMatch>();

                            foreach (var m in matches)
                            {
                                if (m.Length > 1 && m[0].Distance < ratioTestThreshold * m[1].Distance)
                                    goodMatches.Add(m[0]);
                            }

                            if (goodMatches.Count >= minRequiredInliers)
                            {
                                var srcPts = goodMatches
                                    .Select(m => variation.Keypoints[m.QueryIdx].Pt)
                                    .Select(p => new Point2d(p.X, p.Y)).ToArray();
                                var dstPts = goodMatches
                                    .Select(m => targetKeypoints[m.TrainIdx].Pt)
                                    .Select(p => new Point2d(p.X, p.Y)).ToArray();

                                using var mask = new Mat();
                                var homography = Cv2.FindHomography(
                                    InputArray.Create(srcPts), InputArray.Create(dstPts),
                                    HomographyMethods.Ransac, 3.0, mask);

                                if (!homography.Empty())
                                {
                                    var objCorners = new Point2d[] {
                                new Point2d(0, 0),
                                new Point2d(variation.Width, 0),
                                new Point2d(variation.Width, variation.Height),
                                new Point2d(0, variation.Height)
                                    };

                                    var sceneCorners = Cv2.PerspectiveTransform(objCorners, homography);
                                    var pointsForConvex = sceneCorners
                                        .Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToList();

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
                                        System.Runtime.InteropServices.Marshal.Copy(
                                            mask.Data, maskBytes, 0, maskBytes.Length);

                                        foreach (var b in maskBytes)
                                            if (b > 0) inliersCount++;

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

                            if (isMatchFound) break;
                        }

                        if (isMatchFound) matchedBag.Add(path);
                    }
                    catch (Exception) { }

                    int current = Interlocked.Increment(ref processedCount);
                    reportProgress(current, total);
                    return localState;
                },
                localState =>
                {
                    localState.matcher.Dispose();
                    localState.clahe.Dispose();
                    localState.orb.Dispose(); // Не забути!
                }
            );

            // Відновлюємо оригінальний порядок
            var pathIndexMap = collectionPaths
                .Select((path, index) => (path, index))
                .ToDictionary(x => x.path, x => x.index);

            matchedFiles = matchedBag
                .OrderBy(path => pathIndexMap[path])
                .ToList();

            return matchedFiles;
        }



    }
}