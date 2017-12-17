﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using NeuralNetworkNET.APIs;
using NeuralNetworkNET.APIs.Interfaces;
using NeuralNetworkNET.APIs.Misc;
using NeuralNetworkNET.APIs.Results;
using NeuralNetworkNET.Extensions;
using NeuralNetworkNET.Helpers;
using NeuralNetworkNET.Helpers.Imaging;
using NeuralNetworkNET.Networks.Activations;
using NeuralNetworkNET.Networks.Implementations.Layers;
using NeuralNetworkNET.Networks.Implementations.Layers.Abstract;
using NeuralNetworkNET.Structs;
using NeuralNetworkNET.SupervisedLearning.Misc;
using NeuralNetworkNET.SupervisedLearning.Optimization;
using NeuralNetworkNET.SupervisedLearning.Optimization.Parameters;
using NeuralNetworkNET.SupervisedLearning.Progress;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NeuralNetworkNET.Networks.Implementations
{
    /// <summary>
    /// A complete and fully connected neural network with an arbitrary number of hidden layers
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class NeuralNetwork : INeuralNetwork
    {
        #region Public parameters

        /// <inheritdoc/>
        [JsonProperty(nameof(Inputs), Required = Required.Always, Order = 1)]
        public int Inputs { get; }

        /// <inheritdoc/>
        [JsonProperty(nameof(Outputs), Required = Required.Always, Order = 2)]
        public int Outputs { get; }

        /// <inheritdoc/>
        public IReadOnlyList<INetworkLayer> Layers => _Layers;

        #endregion

        /// <summary>
        /// The list of layers that make up the neural network
        /// </summary>
        [NotNull, ItemNotNull]
        [JsonProperty(nameof(Layers), Required = Required.Always, Order = 3)]
        private readonly NetworkLayerBase[] _Layers;

        // The list of layers with weights to update
        private readonly int[] WeightedLayersIndexes;

        /// <summary>
        /// Initializes a new network with the given parameters
        /// </summary>
        /// <param name="layers">The layers that make up the neural network</param>
        internal NeuralNetwork([NotNull, ItemNotNull] params INetworkLayer[] layers)
        {
            // Input check
            if (layers.Length < 1) throw new ArgumentOutOfRangeException(nameof(layers), "The network must have at least one layer");
            foreach ((NetworkLayerBase layer, int i) in layers.Select((l, i) => (l as NetworkLayerBase, i)))
            {
                if (i != layers.Length - 1 && layer is OutputLayerBase) throw new ArgumentException("The output layer must be the last layer in the network");
                if (i == layers.Length - 1 && !(layer is OutputLayerBase)) throw new ArgumentException("The last layer must be an output layer");
                if (i > 0 && layers[i - 1].Outputs != layer.Inputs) throw new ArgumentException($"The inputs of layer #{i} don't match with the outputs of the previous layer");
                if (i > 0 && layer is PoolingLayer && 
                    layers[i - 1] is ConvolutionalLayer convolutional && convolutional.ActivationFunctionType != ActivationFunctionType.Identity)
                    throw new ArgumentException("A convolutional layer followed by a pooling layer must use the Identity activation function");
            }

            // Parameters setup
            Inputs = layers[0].Inputs;
            Outputs = layers[layers.Length - 1].Outputs;
            _Layers = layers.Cast<NetworkLayerBase>().ToArray();
            WeightedLayersIndexes = layers.Select((l, i) => (Layer: l as WeightedLayerBase, Index: i)).Where(t => t.Layer != null).Select(t => t.Index).ToArray();
        }

        #region Public APIs

        /// <inheritdoc/>
        public unsafe float[] Forward(float[] x)
        {
            fixed (float* px = x)
            {
                Tensor.Fix(px, 1, x.Length, out Tensor xSpan);
                Forward(xSpan, out Tensor yHatSpan);
                float[] yHat = yHatSpan.ToArray();
                yHatSpan.Free();
                return yHat;
            }
        }

        /// <inheritdoc/>
        public unsafe float CalculateCost(float[] x, float[] y)
        {
            fixed (float* px = x, py = y)
            {
                Tensor.Fix(px, 1, x.Length, out Tensor xSpan);
                Tensor.Fix(py, 1, y.Length, out Tensor ySpan);
                return CalculateCost(xSpan, ySpan);
            }
        }

        /// <inheritdoc/>
        public unsafe IReadOnlyList<(float[] Z, float[] A)> ExtractDeepFeatures(float[] x)
        {
            fixed (float* px = x)
            {
                Tensor.Fix(px, 1, x.Length, out Tensor xSpan);
                Tensor*
                    zList = stackalloc Tensor[_Layers.Length],
                    aList = stackalloc Tensor[_Layers.Length];
                for (int i = 0; i < _Layers.Length; i++)
                {
                    _Layers[i].Forward(i == 0 ? xSpan : aList[i - 1], out zList[i], out aList[i]);
                }
                (float[], float[])[] features = new(float[], float[])[_Layers.Length];
                for (int i = 0; i < _Layers.Length; i++)
                {
                    features[i] = (zList[i].ToArray(), aList[i].ToArray());
                    zList[i].Free();
                    aList[i].Free();
                }
                return features;
            }
        }

        /// <inheritdoc/>
        public unsafe float[,] Forward(float[,] x)
        {
            fixed (float* px = x)
            {
                Tensor.Fix(px, x.GetLength(0), x.GetLength(1), out Tensor xSpan);
                Forward(xSpan, out Tensor yHatSpan);
                float[,] yHat = yHatSpan.ToArray2D();
                yHatSpan.Free();
                return yHat;
            }
        }

        /// <inheritdoc/>
        public unsafe float CalculateCost(float[,] x, float[,] y)
        {
            fixed (float* px = x, py = y)
            {
                Tensor.Fix(px, x.GetLength(0), x.GetLength(1), out Tensor xSpan);
                Tensor.Fix(py, y.GetLength(0), y.GetLength(1), out Tensor ySpan);
                return CalculateCost(xSpan, ySpan);
            }
        }

        /// <inheritdoc/>
        public unsafe IReadOnlyList<(float[,] Z, float[,] A)> ExtractDeepFeatures(float[,] x)
        {
            fixed (float* px = x)
            {
                Tensor.Fix(px, x.GetLength(0), x.GetLength(1), out Tensor xSpan);
                Tensor*
                    zList = stackalloc Tensor[_Layers.Length],
                    aList = stackalloc Tensor[_Layers.Length];
                for (int i = 0; i < _Layers.Length; i++)
                {
                    _Layers[i].Forward(i == 0 ? xSpan : aList[i - 1], out zList[i], out aList[i]);
                }
                (float[,], float[,])[] features = new(float[,], float[,])[_Layers.Length];
                for (int i = 0; i < _Layers.Length; i++)
                {
                    features[i] = (zList[i].ToArray2D(), aList[i].ToArray2D());
                    zList[i].Free();
                    aList[i].Free();
                }
                return features;
            }
        }

        #endregion

        #region Implementation

        private void Forward(in Tensor x, out Tensor yHat)
        {
            Tensor input = x;
            for (int i = 0; i < _Layers.Length; i++)
            {
                _Layers[i].Forward(input, out Tensor z, out Tensor a); // Forward the inputs through all the network layers
                z.Free();
                if (i > 0) input.Free();
                input = a;
            }
            yHat = input;
        }

        private float CalculateCost(in Tensor x, in Tensor y)
        {
            Forward(x, out Tensor yHat);
            float cost = _Layers[_Layers.Length - 1].To<NetworkLayerBase, OutputLayerBase>().CalculateCost(yHat, y);
            yHat.Free();
            return cost;
        }

        /// <summary>
        /// Calculates the gradient of the cost function with respect to the individual weights and biases
        /// </summary>
        /// <param name="x">The input data</param>
        /// <param name="y">The expected results</param>
        /// <param name="dropout">The dropout probability for eaach neuron in a <see cref="LayerType.FullyConnected"/> layer</param>
        /// <param name="eta">The learning rate for the training session</param>
        /// <param name="l2Factor">The L2 regularization factor</param>
        private unsafe void Backpropagate2(in TrainingBatch batch, float dropout, float eta, float l2Factor)
        {
            fixed (float* px = batch.X, py = batch.Y)
            {
                // Setup
                Tensor*
                    zList = stackalloc Tensor[_Layers.Length],
                    aList = stackalloc Tensor[_Layers.Length],
                    dropoutMasks = stackalloc Tensor[_Layers.Length - 1];
                Tensor.Fix(px, batch.X.GetLength(0), batch.X.GetLength(1), out Tensor x);
                Tensor.Fix(py, batch.Y.GetLength(0), batch.Y.GetLength(1), out Tensor y);
                Tensor** deltas = stackalloc Tensor*[_Layers.Length]; // One delta for each hop through the network

                // Feedforward
                for (int i = 0; i < _Layers.Length; i++)
                {
                    _Layers[i].Forward(i == 0 ? x : aList[i - 1], out zList[i], out aList[i]);
                    if (_Layers[i].LayerType == LayerType.FullyConnected && dropout > 0)
                    {
                        ThreadSafeRandom.NextDropoutMask(aList[i].Entities, aList[i].Length, dropout, out dropoutMasks[i]);
                        aList[i].InPlaceHadamardProduct(dropoutMasks[i]);
                    }
                }

                /* ======================
                 * Calculate delta(L)
                 * ======================
                 * Perform the sigmoid prime of zL, the activity on the last layer
                 * Calculate the gradient of C with respect to a
                 * Compute d(L), the Hadamard product of the gradient and the sigmoid prime for L.
                 * NOTE: for some cost functions (eg. log-likelyhood) the sigmoid prime and the Hadamard product
                 *       with the first part of the formula are skipped as that factor is simplified during the calculation of the output delta */
                _Layers[_Layers.Length - 1].To<NetworkLayerBase, OutputLayerBase>().Backpropagate(aList[_Layers.Length - 1], y, zList[_Layers.Length - 1]);
                deltas[_Layers.Length - 1] = aList + _Layers.Length - 1;
                for (int l = _Layers.Length - 2; l >= 0; l--)
                {
                    /* ======================
                     * Calculate delta(l)
                     * ======================
                     * Perform the sigmoid prime of z(l), the activity on the previous layer
                     * Multiply the previous delta with the transposed weights of the following layer
                     * Compute d(l), the Hadamard product of z'(l) and delta(l + 1) * W(l + 1)T */
                    _Layers[l + 1].Backpropagate(*deltas[l + 1], zList[l], _Layers[l].ActivationFunctions.ActivationPrime);
                    if (dropoutMasks[l].Ptr != IntPtr.Zero) zList[l].InPlaceHadamardProduct(dropoutMasks[l]);
                    deltas[l] = zList + l;
                }

                /* =============================================
                 * Compute the gradients DJDw(l) and DJDb(l)
                 * =============================================
                 * Compute the gradients for each layer with weights and biases.
                 * NOTE: the gradient is only computed for layers with weights and biases, for all the other
                 *       layers a dummy gradient is added to the list and then ignored during the weights update pass */
                Tensor*
                    dJdw = stackalloc Tensor[WeightedLayersIndexes.Length], // One gradient item for layer
                    dJdb = stackalloc Tensor[WeightedLayersIndexes.Length];
                for (int j = 0; j < WeightedLayersIndexes.Length; j++)
                {
                    int i = WeightedLayersIndexes[j];
                    _Layers[i].To<NetworkLayerBase, WeightedLayerBase>().ComputeGradient(i == 0 ? x : aList[i - 1], *deltas[i], out dJdw[j], out dJdb[j]);
                }

                /* ====================
                 * Gradient descent
                 * ====================
                 * Edit the network weights according to the computed gradients and the current training parameters. 
                 * The learning rate indicates the desired convergence speed, while the L2 factor is used to regularize the network and keep the weights small */
                float alpha = eta / batch.X.GetLength(0);
                void Kernel(int j)
                {
                    int i = WeightedLayersIndexes[j];
                    _Layers[i].To<NetworkLayerBase, WeightedLayerBase>().Minimize(dJdw[j], dJdb[j], alpha, l2Factor);
                    dJdw[j].Free();
                    dJdb[j].Free();
                }
                Parallel.For(0, WeightedLayersIndexes.Length, Kernel).AssertCompleted();

                // Cleanup
                for (int i = 0; i < _Layers.Length - 1; i++)
                {
                    zList[i].Free();
                    aList[i].Free();
                    if (dropoutMasks[i].Ptr != IntPtr.Zero) dropoutMasks[i].Free();
                }
                zList[_Layers.Length - 1].Free();
                aList[_Layers.Length - 1].Free();
            }
        }

        internal delegate void WeightsUpdater(int i, in Tensor dJdw, in Tensor dJdb, int samples, [NotNull] WeightedLayerBase layer);

        /// <summary>
        /// Calculates the gradient of the cost function with respect to the individual weights and biases
        /// </summary>
        /// <param name="x">The input data</param>
        /// <param name="y">The expected results</param>
        /// <param name="dropout">The dropout probability for eaach neuron in a <see cref="LayerType.FullyConnected"/> layer</param>
        /// <param name="eta">The learning rate for the training session</param>
        /// <param name="l2Factor">The L2 regularization factor</param>
        private unsafe void Backpropagate(in TrainingBatch batch, float dropout, [NotNull] WeightsUpdater update)
        {
            fixed (float* px = batch.X, py = batch.Y)
            {
                // Setup
                Tensor*
                    zList = stackalloc Tensor[_Layers.Length],
                    aList = stackalloc Tensor[_Layers.Length],
                    dropoutMasks = stackalloc Tensor[_Layers.Length - 1];
                Tensor.Fix(px, batch.X.GetLength(0), batch.X.GetLength(1), out Tensor x);
                Tensor.Fix(py, batch.Y.GetLength(0), batch.Y.GetLength(1), out Tensor y);
                Tensor** deltas = stackalloc Tensor*[_Layers.Length]; // One delta for each hop through the network

                // Feedforward
                for (int i = 0; i < _Layers.Length; i++)
                {
                    _Layers[i].Forward(i == 0 ? x : aList[i - 1], out zList[i], out aList[i]);
                    if (_Layers[i].LayerType == LayerType.FullyConnected && dropout > 0)
                    {
                        ThreadSafeRandom.NextDropoutMask(aList[i].Entities, aList[i].Length, dropout, out dropoutMasks[i]);
                        aList[i].InPlaceHadamardProduct(dropoutMasks[i]);
                    }
                }

                /* ======================
                 * Calculate delta(L)
                 * ======================
                 * Perform the sigmoid prime of zL, the activity on the last layer
                 * Calculate the gradient of C with respect to a
                 * Compute d(L), the Hadamard product of the gradient and the sigmoid prime for L.
                 * NOTE: for some cost functions (eg. log-likelyhood) the sigmoid prime and the Hadamard product
                 *       with the first part of the formula are skipped as that factor is simplified during the calculation of the output delta */
                _Layers[_Layers.Length - 1].To<NetworkLayerBase, OutputLayerBase>().Backpropagate(aList[_Layers.Length - 1], y, zList[_Layers.Length - 1]);
                deltas[_Layers.Length - 1] = aList + _Layers.Length - 1;
                for (int l = _Layers.Length - 2; l >= 0; l--)
                {
                    /* ======================
                     * Calculate delta(l)
                     * ======================
                     * Perform the sigmoid prime of z(l), the activity on the previous layer
                     * Multiply the previous delta with the transposed weights of the following layer
                     * Compute d(l), the Hadamard product of z'(l) and delta(l + 1) * W(l + 1)T */
                    _Layers[l + 1].Backpropagate(*deltas[l + 1], zList[l], _Layers[l].ActivationFunctions.ActivationPrime);
                    if (dropoutMasks[l].Ptr != IntPtr.Zero) zList[l].InPlaceHadamardProduct(dropoutMasks[l]);
                    deltas[l] = zList + l;
                }

                /* =============================================
                 * Compute the gradients DJDw(l) and DJDb(l)
                 * =============================================
                 * Compute the gradients for each layer with weights and biases.
                 * NOTE: the gradient is only computed for layers with weights and biases, for all the other
                 *       layers a dummy gradient is added to the list and then ignored during the weights update pass */
                Tensor*
                    dJdw = stackalloc Tensor[WeightedLayersIndexes.Length], // One gradient item for layer
                    dJdb = stackalloc Tensor[WeightedLayersIndexes.Length];
                for (int j = 0; j < WeightedLayersIndexes.Length; j++)
                {
                    int i = WeightedLayersIndexes[j];
                    _Layers[i].To<NetworkLayerBase, WeightedLayerBase>().ComputeGradient(i == 0 ? x : aList[i - 1], *deltas[i], out dJdw[j], out dJdb[j]);
                }

                /* ====================
                 * Gradient descent
                 * ====================
                 * Edit the network weights according to the computed gradients and the current training parameters */
                int samples = batch.X.GetLength(0);
                Parallel.For(0, WeightedLayersIndexes.Length, i =>
                {
                    int l = WeightedLayersIndexes[i];
                    update(i, dJdw[i], dJdb[i], samples, _Layers[l].To<NetworkLayerBase, WeightedLayerBase>());
                    dJdw[i].Free();
                    dJdb[i].Free();
                }).AssertCompleted();

                // Cleanup
                for (int i = 0; i < _Layers.Length - 1; i++)
                {
                    zList[i].Free();
                    aList[i].Free();
                    if (dropoutMasks[i].Ptr != IntPtr.Zero) dropoutMasks[i].Free();
                }
                zList[_Layers.Length - 1].Free();
                aList[_Layers.Length - 1].Free();
            }
        }

        #endregion

        #region Training

        /// <summary>
        /// Trains the current network using the gradient descent algorithm
        /// </summary>
        /// <param name="miniBatches">The training baatches for the current session</param>
        /// <param name="epochs">The desired number of training epochs to run</param>
        /// <param name="eta">The learning rate</param>
        /// <param name="dropout">Indicates the dropout probability for neurons in a <see cref="LayerType.FullyConnected"/> layer</param>
        /// <param name="lambda">The L2 regularization factor</param>
        /// <param name="trainingProgress">An optional progress callback to monitor progress on the training dataset</param>
        /// <param name="validationParameters">The optional <see cref="ValidationParameters"/> instance (used for early-stopping)</param>
        /// <param name="testParameters">The optional <see cref="TestParameters"/> instance (used to monitor the training progress)</param>
        /// <param name="token">The <see cref="CancellationToken"/> for the training session</param>
        [NotNull]
        [CollectionAccess(CollectionAccessType.Read)]
        internal TrainingSessionResult Optimize(
            BatchesCollection miniBatches,
            int epochs,
            [NotNull] WeightsUpdater update,
            float dropout = 0,
            [CanBeNull] IProgress<BackpropagationProgressEventArgs> trainingProgress = null,
            [CanBeNull] ValidationParameters validationParameters = null,
            [CanBeNull] TestParameters testParameters = null,         
            CancellationToken token = default)
        {
            // Setup
            DateTime startTime = DateTime.Now;
            List<DatasetEvaluationResult>
                validationReports = new List<DatasetEvaluationResult>(),
                testReports = new List<DatasetEvaluationResult>();
            TrainingSessionResult PrepareResult(TrainingStopReason reason, int loops)
            {
                return new TrainingSessionResult(reason, loops, DateTime.Now.Subtract(startTime).RoundToSeconds(), validationReports, testReports);
            }

            // Convergence manager for the validation dataset
            RelativeConvergence convergence = validationParameters == null
                ? null
                : new RelativeConvergence(validationParameters.Tolerance, validationParameters.EpochsInterval);

            // Create the training batches
            for (int i = 0; i < epochs; i++)
            {
                // Shuffle the training set
                miniBatches.CrossShuffle();

                // Gradient descent over the current batches
                for (int j = 0; j < miniBatches.Count; j++)
                {
                    if (token.IsCancellationRequested) return PrepareResult(TrainingStopReason.TrainingCanceled, i);
                    Backpropagate(miniBatches.Batches[j], dropout, update);
                }

                // Check for overflows
                if (!Parallel.For(0, _Layers.Length, (j, state) =>
                {
                    if (_Layers[j] is WeightedLayerBase layer && !layer.ValidateWeights()) state.Break();
                }).IsCompleted) return PrepareResult(TrainingStopReason.NumericOverflow, i);

                // Check the training progress
                if (trainingProgress != null)
                {
                    (float cost, _, float accuracy) = Evaluate(miniBatches);
                    trainingProgress.Report(new BackpropagationProgressEventArgs(i + 1, cost, accuracy));
                }

                // Check the validation dataset
                if (convergence != null)
                {
                    (float cost, _, float accuracy) = Evaluate(validationParameters.Dataset);
                    validationReports.Add(new DatasetEvaluationResult(cost, accuracy));
                    convergence.Value = accuracy;
                    if (convergence.HasConverged) return PrepareResult(TrainingStopReason.EarlyStopping, i);
                }

                // Report progress if necessary
                if (testParameters != null)
                {
                    (float cost, _, float accuracy) = Evaluate(testParameters.Dataset);
                    testReports.Add(new DatasetEvaluationResult(cost, accuracy));
                    testParameters.ProgressCallback.Report(new BackpropagationProgressEventArgs(i + 1, cost, accuracy));
                }
            }
            return PrepareResult(TrainingStopReason.EpochsCompleted, epochs);
        }

        /// <summary>
        /// Trains the current network using the gradient descent algorithm
        /// </summary>
        /// <param name="miniBatches">The training baatches for the current session</param>
        /// <param name="epochs">The desired number of training epochs to run</param>
        /// <param name="eta">The learning rate</param>
        /// <param name="dropout">Indicates the dropout probability for neurons in a <see cref="LayerType.FullyConnected"/> layer</param>
        /// <param name="lambda">The L2 regularization factor</param>
        /// <param name="trainingProgress">An optional progress callback to monitor progress on the training dataset</param>
        /// <param name="validationParameters">The optional <see cref="ValidationParameters"/> instance (used for early-stopping)</param>
        /// <param name="testParameters">The optional <see cref="TestParameters"/> instance (used to monitor the training progress)</param>
        /// <param name="token">The <see cref="CancellationToken"/> for the training session</param>
        [NotNull]
        internal TrainingSessionResult StochasticGradientDescent(
            BatchesCollection miniBatches,
            int epochs,
            float eta = 0.5f, float dropout = 0, float lambda = 0,
            [CanBeNull] IProgress<BackpropagationProgressEventArgs> trainingProgress = null,
            [CanBeNull] ValidationParameters validationParameters = null,
            [CanBeNull] TestParameters testParameters = null,
            CancellationToken token = default)
        {            
            unsafe void Minimize(int i, in Tensor dJdw, in Tensor dJdb, int samples, WeightedLayerBase layer)
            {
                // Tweak the weights
                float
                    alpha = eta / samples,
                    l2Factor = eta * lambda / samples;
                fixed (float* pw = layer.Weights)
                {
                    float* pdj = dJdw;
                    int w = layer.Weights.Length;
                    for (int x = 0; x < w; x++)
                        pw[x] -= l2Factor * pw[x] + alpha * pdj[x];
                }

                // Tweak the biases of the lth layer
                fixed (float* pb = layer.Biases)
                {
                    float* pdj = dJdb;
                    int w = layer.Biases.Length;
                    for (int x = 0; x < w; x++)
                        pb[x] -= alpha * pdj[x];
                }
            }

            return Optimize(miniBatches, epochs, Minimize, dropout, trainingProgress, validationParameters, testParameters, token);
        }

        [NotNull]
        internal unsafe TrainingSessionResult Adadelta(
            BatchesCollection miniBatches,
            int epochs, float dropout = 0, float rho = 0.95f, float epsilon = 1e-8f, float l2Factor = 0,
            [CanBeNull] IProgress<BackpropagationProgressEventArgs> trainingProgress = null,
            [CanBeNull] ValidationParameters validationParameters = null,
            [CanBeNull] TestParameters testParameters = null,
            CancellationToken token = default)
        {
            // Initialize Adadelta parameters
            Tensor*
                egSquaredW = stackalloc Tensor[WeightedLayersIndexes.Length],
                eDeltaxSquaredW = stackalloc Tensor[WeightedLayersIndexes.Length],
                egSquaredB = stackalloc Tensor[WeightedLayersIndexes.Length],
                eDeltaxSquaredB = stackalloc Tensor[WeightedLayersIndexes.Length];
            for (int i = 0; i < WeightedLayersIndexes.Length; i++)
            {
                WeightedLayerBase layer = _Layers[WeightedLayersIndexes[i]].To<NetworkLayerBase, WeightedLayerBase>();
                Tensor.NewZeroed(layer.Weights.GetLength(0), layer.Weights.GetLength(1), out egSquaredW[i]);
                Tensor.NewZeroed(layer.Weights.GetLength(0), layer.Weights.GetLength(1), out eDeltaxSquaredW[i]);
                Tensor.NewZeroed(1, layer.Biases.Length, out egSquaredB[i]);
                Tensor.NewZeroed(1, layer.Biases.Length, out eDeltaxSquaredB[i]);
            }

            unsafe void Minimize(int i, in Tensor dJdw, in Tensor dJdb, int samples, WeightedLayerBase layer)
            {
                fixed (float* pw = layer.Weights)
                {
                    float*
                        pdj = dJdw,
                        egSqrt = egSquaredW[i],
                        eDSqrtx = eDeltaxSquaredW[i];
                    int w = layer.Weights.Length;
                    for (int x = 0; x < w; x++)
                    {
                        float gt = pdj[x];
                        egSqrt[x] = rho * egSqrt[x] + (1 - rho) * gt * gt;
                        float 
                            rmsDx_1 = (float)Math.Sqrt(eDSqrtx[x] + epsilon),
                            rmsGt = (float)Math.Sqrt(egSqrt[x] + epsilon),
                            dx = -(rmsDx_1 / rmsGt) * gt;
                        eDSqrtx[x] = rho * eDSqrtx[x] + (1 - rho) * dx * dx;
                        pw[x] += dx - l2Factor * pw[x];
                    }
                }

                // Tweak the biases of the lth layer
                fixed (float* pb = layer.Biases)
                {
                    float*
                        pdj = dJdb,
                        egSqrt = egSquaredB[i],
                        eDSqrtb = eDeltaxSquaredB[i];
                    int w = layer.Biases.Length;
                    for (int b = 0; b < w; b++)
                    {
                        float gt = pdj[b];
                        egSqrt[b] = rho * egSqrt[b] + (1 - rho) * gt * gt;
                        float
                            rmsDx_1 = (float)Math.Sqrt(eDSqrtb[b] + epsilon),
                            rmsGt = (float)Math.Sqrt(egSqrt[b] + epsilon),
                            db = -(rmsDx_1 / rmsGt) * gt;
                        eDSqrtb[b] = rho * eDSqrtb[b] + (1 - rho) * db * db;
                        pb[b] += db - l2Factor * pb[b];
                    }
                }
            }

            return Optimize(miniBatches, epochs, Minimize, dropout, trainingProgress, validationParameters, testParameters, token);
        }

        #endregion

        #region Evaluation

        // Auxiliary function to forward a test batch
        private unsafe (float Cost, int Classified) Evaluate(in Tensor x, in Tensor y)
        {
            // Feedforward
            Forward(x, out Tensor yHat);

            // Function that counts the correctly classified items
            float* pyHat = yHat, pY = y;
            int wy = y.Length, total = 0;
            void Kernel(int i)
            {
                int
                    offset = i * wy,
                    maxHat = MatrixExtensions.Argmax(pyHat + offset, wy),
                    max = MatrixExtensions.Argmax(pY + offset, wy);
                if (max == maxHat) Interlocked.Increment(ref total);
            }

            // Check the correctly classified samples and calculate the cost
            Parallel.For(0, x.Entities, Kernel).AssertCompleted();
            float cost = _Layers[_Layers.Length - 1].To<NetworkLayerBase, OutputLayerBase>().CalculateCost(yHat, y);
            yHat.Free();
            return (cost, total);
        }

        /// <summary>
        /// Calculates the current network performances with the given test samples
        /// </summary>
        /// <param name="evaluationSet">The inputs and expected outputs to test the network</param>
        /// <param name="batchSize">The number of test samples to forward in parallel</param>
        internal unsafe (float Cost, int Classified, float Accuracy) Evaluate((float[,] X, float[,] Y) evaluationSet)
        {
            // Actual test evaluation
            int batchSize = NetworkManager.MaximumBatchSize;
            fixed (float* px = evaluationSet.X, py = evaluationSet.Y)
            {
                int
                    h = evaluationSet.X.GetLength(0),
                    wx = evaluationSet.X.GetLength(1),
                    wy = evaluationSet.Y.GetLength(1),
                    batches = h / batchSize,
                    batchMod = h % batchSize,
                    classified = 0;
                float cost = 0;

                // Process the even batches
                for (int i = 0; i < batches; i++)
                {
                    Tensor.Fix(px + i * batchSize * wx, batchSize, wx, out Tensor xSpan);
                    Tensor.Fix(py + i * batchSize * wy, batchSize, wy, out Tensor ySpan);
                    (float pCost, int pClassified) = Evaluate(xSpan, ySpan);
                    cost += pCost;
                    classified += pClassified;
                }

                // Process the remaining samples, if any
                if (batchMod > 0)
                {
                    Tensor.Fix(px + batches * batchSize * wx, batchMod, wx, out Tensor xSpan);
                    Tensor.Fix(py + batches * batchSize * wy, batchMod, wy, out Tensor ySpan);
                    (float pCost, int pClassified) = Evaluate(xSpan, ySpan);
                    cost += pCost;
                    classified += pClassified;
                }
                return (cost, classified, (float)classified / h * 100);
            }
        }

        /// <summary>
        /// Calculates the current network performances with the given test samples
        /// </summary>
        /// <param name="batchSize">The training batches currently used to train the network</param>
        internal unsafe (float Cost, int Classified, float Accuracy) Evaluate([NotNull] BatchesCollection batches)
        {
            // Actual test evaluation
            int
                batchSize = NetworkManager.MaximumBatchSize,
                classified = 0;
            float cost = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                ref readonly TrainingBatch batch = ref batches.Batches[i];
                fixed (float* px = batch.X, py = batch.Y)
                {
                    Tensor.Fix(px, batch.X.GetLength(0), batch.X.GetLength(1), out Tensor xSpan);
                    Tensor.Fix(py, xSpan.Entities, batch.Y.GetLength(1), out Tensor ySpan);
                    var partial = Evaluate(xSpan, ySpan);
                    cost += partial.Cost;
                    classified += partial.Classified;
                }
            }
            return (cost, classified, (float)classified / batches.Samples * 100);
        }

        #endregion

        #region Tools

        /// <inheritdoc/>
        public String SerializeAsJson() => JsonConvert.SerializeObject(this, Formatting.Indented, new StringEnumConverter());

        /// <inheritdoc/>
        public bool Equals(INeuralNetwork other)
        {
            // Compare general features
            if (other is NeuralNetwork network &&
                other.Inputs == Inputs &&
                other.Outputs == Outputs &&
                _Layers.Length == network._Layers.Length)
            {
                // Compare the individual layers
                return _Layers.Zip(network._Layers, (l1, l2) => l1.Equals(l2)).All(b => b);
            }
            return false;
        }

        /// <inheritdoc/>
        public String Save(DirectoryInfo directory, String name)
        {
            String path = $"{Path.Combine(directory.ToString(), name)}{NeuralNetworkLoader.NetworkFileExtension}";
            using (FileStream stream = File.OpenWrite(path))
            {
                stream.Write(_Layers.Length);
                foreach (NetworkLayerBase layer in _Layers)
                {
                    stream.WriteByte((byte)layer.LayerType);
                    stream.WriteByte((byte)layer.ActivationFunctionType);
                    stream.Write(layer.Inputs);
                    stream.Write(layer.Outputs);
                    if (layer is PoolingLayer pooling)
                    {
                        stream.Write(pooling.InputVolume.Height);
                        stream.Write(pooling.InputVolume.Width);
                        stream.Write(pooling.InputVolume.Depth);
                    }
                    if (layer is ConvolutionalLayer convolutional)
                    {
                        stream.Write(convolutional.InputVolume.Height);
                        stream.Write(convolutional.InputVolume.Width);
                        stream.Write(convolutional.InputVolume.Depth);
                        stream.Write(convolutional.OutputVolume.Height);
                        stream.Write(convolutional.OutputVolume.Width);
                        stream.Write(convolutional.OutputVolume.Depth);
                        stream.Write(convolutional.KernelVolume.Height);
                        stream.Write(convolutional.KernelVolume.Width);
                        stream.Write(convolutional.KernelVolume.Depth);
                    }
                    if (layer is WeightedLayerBase weighted)
                    {
                        stream.Write(weighted.Weights);
                        stream.Write(weighted.Biases);
                    }
                    if (layer is OutputLayerBase output)
                    {
                        stream.WriteByte((byte)output.CostFunctionType);
                    }
                }
            }
            return path;
        }

        /// <inheritdoc/>
        public void ExportWeightsAsImages(DirectoryInfo directory, ImageScaling scaling)
        {
            foreach ((INetworkLayer layer, int i) in Layers.Select((l, i) => (l, i)))
            {
                switch (layer)
                {
                    case ConvolutionalLayer convolutional when i == 0:
                        ImageLoader.ExportGrayscaleKernels(Path.Combine(directory.ToString(), $"{i} - Convolutional"), convolutional.Weights, convolutional.KernelVolume, scaling);
                        break;
                    case ConvolutionalLayer _:
                        throw new NotImplementedException();
                    case OutputLayer output:
                        ImageLoader.ExportFullyConnectedWeights(Path.Combine(directory.ToString(), $"{i} - Output"), output.Weights, output.Biases, scaling);
                        break;
                    case SoftmaxLayer softmax:
                        ImageLoader.ExportFullyConnectedWeights(Path.Combine(directory.ToString(), $"{i} - Softmax"), softmax.Weights, softmax.Biases, scaling);
                        break;
                    case FullyConnectedLayer fullyConnected:
                        ImageLoader.ExportFullyConnectedWeights(Path.Combine(directory.ToString(), $"{i} - Fully connected"), fullyConnected.Weights, fullyConnected.Biases, scaling);
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public INeuralNetwork Clone() => new NeuralNetwork(_Layers.Select(l => l.Clone()).ToArray());

        #endregion
    }
}
