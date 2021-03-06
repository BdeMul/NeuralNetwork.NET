﻿using System;
using System.Threading.Tasks;
using NeuralNetworkNET.APIs;
using NeuralNetworkNET.APIs.Datasets;
using NeuralNetworkNET.APIs.Enums;
using NeuralNetworkNET.APIs.Interfaces;
using NeuralNetworkNET.APIs.Interfaces.Data;
using NeuralNetworkNET.APIs.Results;
using NeuralNetworkNET.APIs.Structs;
using NeuralNetworkNET.SupervisedLearning.Progress;
using SixLabors.ImageSharp.PixelFormats;

namespace DigitsTest
{
    public class Program
    {
        public static async Task Main()
        {
            // Create the network
            INeuralNetwork network = NetworkManager.NewSequential(TensorInfo.Image<Alpha8>(28, 28),
                NetworkLayers.Convolutional((5, 5), 20, ActivationType.Identity),
                NetworkLayers.Pooling(ActivationType.LeakyReLU),
                NetworkLayers.FullyConnected(100, ActivationType.LeCunTanh),
                NetworkLayers.Softmax(10));

            // Prepare the dataset
            ITrainingDataset trainingData = await Mnist.GetTrainingDatasetAsync(100); // Batches of 100 samples
            ITestDataset testData = await Mnist.GetTestDatasetAsync(p => Printf($"Epoch {p.Iteration}, cost: {p.Result.Cost}, accuracy: {p.Result.Accuracy}"));
            if (trainingData == null || testData == null)
            {
                Printf("Error downloading the datasets");
                Console.ReadKey();
                return;
            }

            // Train the network
            TrainingSessionResult result = await NetworkManager.TrainNetworkAsync(network,
                trainingData,
                TrainingAlgorithms.AdaDelta(),
                60, 0.5f,
                TrackBatchProgress,
                testDataset: testData);
            Printf($"Stop reason: {result.StopReason}, elapsed time: {result.TrainingTime}");
            Console.ReadKey();
        }

        // Prints an output message
        private static void Printf(String text)
        {
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.Write(">> ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{text}\n");
        }

        // Training monitor
        private static void TrackBatchProgress(BatchProgress progress)
        {
            Console.SetCursorPosition(0, Console.CursorTop);
            int n = (int)(progress.Percentage * 32 / 100); // 32 is the number of progress '=' characters to display
            char[] c = new char[32];
            for (int i = 0; i < 32; i++) c[i] = i <= n ? '=' : ' ';
            Console.Write($"[{new String(c)}] ");
        }
    }
}
