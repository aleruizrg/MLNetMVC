﻿using Microsoft.AspNetCore.Http;
using Microsoft.ML;
using Microsoft.ML.Data;
using MLNetMVC.Data.EF;
using MLNetMVC.Data.VM;
using static System.Formats.Asn1.AsnWriter;

namespace MLNetMVC.Logica
{
    public interface IMLNetLogica
    {
        ITransformer GenerateModel();
        Task<ImagePredictionVM> ClassifySingleImage(IFormFile imageFile, ITransformer model);
        void DisplayResults(IEnumerable<ImagePredictionVM> imagePredictionData);
        Task AgregarElemento(string ImagePath, float Score, string PredictedLabelValue);
        Task CorregirElemento(string ImagePath, string label);
    }

    public class MLNetLogica : IMLNetLogica
    {
        private TranferLearningMlContext _context;
        private MLContext _mlContext;


        public MLNetLogica(TranferLearningMlContext context, MLContext mlContext)
        {
            _context = context;
            _mlContext = mlContext;
        }

        static string _assetsPath = Path.Combine(Environment.CurrentDirectory, "assets");
        static string _imagesFolder = Path.Combine(_assetsPath, "images");
        static string _trainTagsTsv = Path.Combine(_imagesFolder, "tags.tsv");
        static string _testTagsTsv = Path.Combine(_imagesFolder, "test-tags.tsv");
        static string _predictSingleImage = Path.Combine(_imagesFolder, "toaster3.jpg");
        static string _inceptionTensorFlowModel = Path.Combine(_assetsPath, "inception", "tensorflow_inception_graph.pb");

        //creo el contexto para trabajar con ML
        public ITransformer GenerateModel()
        {

            IEstimator<ITransformer> pipeline = _mlContext.Transforms.LoadImages(outputColumnName: "input", imageFolder: _imagesFolder, inputColumnName: nameof(ImageDatumVM.ImagePath))
                        // The image transforms transform the images into the model's expected format.
                        .Append(_mlContext.Transforms.ResizeImages(outputColumnName: "input", imageWidth: InceptionSettings.ImageWidth, imageHeight: InceptionSettings.ImageHeight, inputColumnName: "input"))
                        .Append(_mlContext.Transforms.ExtractPixels(outputColumnName: "input", interleavePixelColors: InceptionSettings.ChannelsLast, offsetImage: InceptionSettings.Mean))
                        .Append(_mlContext.Model.LoadTensorFlowModel(_inceptionTensorFlowModel).
            ScoreTensorFlowModel(outputColumnNames: new[] { "softmax2_pre_activation" }, inputColumnNames: new[] { "input" }, addBatchDimensionInput: true))
                        .Append(_mlContext.Transforms.Conversion.MapValueToKey(outputColumnName: "LabelKey", inputColumnName: "Label"))
                        .Append(_mlContext.MulticlassClassification.Trainers.LbfgsMaximumEntropy(labelColumnName: "LabelKey", featureColumnName: "softmax2_pre_activation"))
                        .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabelValue", "PredictedLabel"))
        .AppendCacheCheckpoint(_mlContext);

            IDataView trainingData = _mlContext.Data.LoadFromTextFile<ImageDatumVM>(path: _trainTagsTsv, hasHeader: false);

            ITransformer model = pipeline.Fit(trainingData);

            IDataView testData = _mlContext.Data.LoadFromTextFile<ImageDatumVM>(path: _testTagsTsv, hasHeader: false);
            IDataView predictions = model.Transform(testData);

            // Create an IEnumerable for the predictions for displaying results
            IEnumerable<ImagePredictionVM> imagePredictionData = _mlContext.Data.CreateEnumerable<ImagePredictionVM>(predictions, true);

            //Muestro los resultados de lass predicciones
            DisplayResults(imagePredictionData);

            MulticlassClassificationMetrics metrics =
            _mlContext.MulticlassClassification.Evaluate(predictions,
                labelColumnName: "LabelKey",
                predictedLabelColumnName: "PredictedLabel");

            Console.WriteLine($"LogLoss is: {metrics.LogLoss}");
            Console.WriteLine($"PerClassLogLoss is: {String.Join(" , ", metrics.PerClassLogLoss.Select(c => c.ToString()))}");

            return model;
        }

        public void DisplayResults(IEnumerable<ImagePredictionVM> imagePredictionData)
        {
            foreach (ImagePredictionVM prediction in imagePredictionData)
            {
                Console.WriteLine($"Image: {Path.GetFileName(prediction.ImagePath)} predicted as: {prediction.PredictedLabelValue} with score: {prediction.Score?.Max()} ");
            }
        }

        public async Task<ImagePredictionVM> ClassifySingleImage(IFormFile imageFile, ITransformer model)
        {
            var filePath = Path.Combine(_imagesFolder, imageFile.FileName);
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await imageFile.CopyToAsync(stream);
            }

            var imageData = new ImageDatumVM()
            {
                ImagePath = filePath
            };

            string trainFile = _trainTagsTsv;

            // La nueva línea que deseas agregar
            string nuevaLinea = imageFile.FileName;

            // Usar StreamWriter para agregar la nueva línea al final del archivo
            using (StreamWriter sw = new StreamWriter(trainFile, true))
            {
                sw.WriteLine();
                sw.Write(nuevaLinea);
            }

            var predictor = _mlContext.Model.CreatePredictionEngine<ImageDatumVM, ImagePredictionVM>(model);
            var prediction = predictor.Predict(imageData);

            byte[] imageBytes = File.ReadAllBytes(imageData.ImagePath);
            prediction.ImagePath = Convert.ToBase64String(imageBytes);

            return prediction;
        }

        public async Task AgregarElemento(string ImagePath, float Score, string PredictedLabelValue)
        {
            var imagen = new ImageDatum
            {
                ImagePath = ImagePath,
                Label = PredictedLabelValue
            };

            var prediccion = new ImagePrediction
            {
                ImageData = imagen,
                Score = Score.ToString(),
                PredictedLabelValue = PredictedLabelValue
            };

            string trainFile = _trainTagsTsv;

            // La nueva línea que deseas agregar
            string nuevaLinea = "\t" + PredictedLabelValue;

            // Usar StreamWriter para agregar la nueva línea al final del archivo
            using (StreamWriter sw = new StreamWriter(trainFile, true))
            {
                sw.Write(nuevaLinea);
            }

            await _context.ImageData.AddAsync(imagen);
            await _context.ImagePredictions.AddAsync(prediccion);
            _context.SaveChanges();
        }

        public async Task CorregirElemento( string ImagePath, string label)
        {
            var imagen = new ImageDatum
            {
                ImagePath = ImagePath,
                Label = label
            };

            string filePath = _trainTagsTsv;

            // La nueva línea que deseas agregar
            string nuevaLinea = "\t" + label ;

            // Usar StreamWriter para agregar la nueva línea al final del archivo
            using (StreamWriter sw = new StreamWriter(filePath, true))
            {
                sw.Write(nuevaLinea);
            }

            await _context.ImageData.AddAsync(imagen);
            _context.SaveChanges();
        }

        struct InceptionSettings
        {
            public const int ImageHeight = 224;
            public const int ImageWidth = 224;
            public const float Mean = 117;
            public const float Scale = 1;
            public const bool ChannelsLast = true;
        }
    }
}
