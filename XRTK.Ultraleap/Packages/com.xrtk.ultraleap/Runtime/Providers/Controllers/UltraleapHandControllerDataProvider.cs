﻿// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using UnityEngine;
using Leap;
using XRTK.Definitions.Devices;
using XRTK.Definitions.Utilities;
using XRTK.Ultraleap.Profiles;
using XRTK.Providers.Controllers.Hands;
using XRTK.Services;
using XRTK.Ultraleap.Definitions;
using XRTK.Interfaces.InputSystem;
using XRTK.Definitions.Platforms;
using XRTK.Attributes;
using XRTK.Definitions.Controllers.Hands;
using XRTK.Ultraleap.Extensions;
using XRTK.Utilities;
using XRTK.Extensions;

namespace XRTK.Ultraleap.Providers.Controllers
{
    /// <summary>
    /// Enables the XRTK hand tracking implementation to run powered by Ultraleap hand tracking
    /// sensors, like e.g. the Leap Motion device, by providing <see cref="HandData"/> to the
    /// <see cref="MixedRealityHandController"/>.
    /// </summary>
    [RuntimePlatform(typeof(UltraleapPlatform))]
    [System.Runtime.InteropServices.Guid("61cec407-ffa4-4a5c-b96a-5229348f85c2")]
    public class UltraleapHandControllerDataProvider : BaseHandControllerDataProvider
    {
        /// <inheritdoc />
        public UltraleapHandControllerDataProvider(string name, uint priority, UltraleapHandControllerDataProviderProfile profile, IMixedRealityInputSystem parentService)
            : base(name, priority, profile, parentService)
        {
            OperationMode = profile.OperationMode;
            LeapControllerOffset = profile.LeapControllerOffset;
            DeviceOffsetMode = profile.DeviceOffsetMode;
            DeviceTiltXAxis = profile.DeviceTiltXAxis;
            DeviceOffsetYAxis = profile.DeviceOffsetYAxis;
            DeviceOffsetZAxis = profile.DeviceOffsetZAxis;
            FrameOptimizationMode = profile.FrameOptimizationMode;
            MaxReconnectionAttempts = profile.MaxReconnectionAttempts;
            ReconnectionInterval = profile.ReconnectionInterval;

            postProcessor = new HandDataPostProcessor(TrackedPoses)
            {
                PlatformProvidesPointerPose = true
            };
        }

        private const int leapThumbIndex = (int)Finger.FingerType.TYPE_THUMB;
        private const int leapIndexFingerIndex = (int)Finger.FingerType.TYPE_INDEX;
        private const int leapMiddleFingerIndex = (int)Finger.FingerType.TYPE_MIDDLE;
        private const int leapRingFingerIndex = (int)Finger.FingerType.TYPE_RING;
        private const int leapLittleFingerIndex = (int)Finger.FingerType.TYPE_PINKY;

        private const int leapMetacarpalBoneIndex = (int)Bone.BoneType.TYPE_METACARPAL;
        private const int leapProximalBoneIndex = (int)Bone.BoneType.TYPE_PROXIMAL;
        private const int leapIntermediateBoneIndex = (int)Bone.BoneType.TYPE_INTERMEDIATE;
        private const int leapDistalBoneIndex = (int)Bone.BoneType.TYPE_DISTAL;

        private const float defaultDeviceOffsetYAxis = 0f;
        private const float defaultDeviceOffsetZAxis = 0.12f;
        private const float defaultDeviceTiltXAxis = 5f;

        private readonly HandDataPostProcessor postProcessor;
        private readonly Dictionary<Handedness, int> handIdMap = new Dictionary<Handedness, int>();
        private readonly Dictionary<Handedness, MixedRealityHandController> activeControllers = new Dictionary<Handedness, MixedRealityHandController>();
        private readonly MixedRealityPose[] jointPoses = new MixedRealityPose[HandData.JointCount];

        private Controller ultraleapController;
        private Frame untransformedFixedFrame;
        private Frame transformedFixedFrame;
        private Frame untransformedUpdateFrame;
        private Frame transformedUpdateFrame;
        private int framesSinceServiceConnectionChecked = 0;
        private int numberOfReconnectionAttempts = 0;

        private GameObject originConversionProxy;
        private GameObject handRootConversionProxy;
        private GameObject jointConversionProxy;

        /// <summary>
        /// Gets the ultraleap controller's current operation mode.
        /// </summary>
        private UltraleapOperationMode OperationMode { get; }

        /// <summary>
        /// The maximum number of times the provider will attempt to reconnect to the service before giving up.
        /// </summary>
        private int MaxReconnectionAttempts { get; }

        /// <summary>
        /// The number of frames to wait between each reconnection attempt.
        /// </summary>
        private int ReconnectionInterval { get; }

        /// <summary>
        /// Gets the data provider's currently configured frame optmization mode.
        /// </summary>
        private UltraleapFrameOptimizationMode FrameOptimizationMode { get; }

        /// <summary>
        /// Offset applied to the rendered hands when in <see cref="UltraleapOperationMode.Desktop"/> mode,
        /// this is mostly used to have the hands appear in front of the camera.
        /// </summary>
        private Vector3 LeapControllerOffset { get; }

        /// <summary>
        /// Applies only to <see cref="UltraleapOperationMode.HeadsetMounted"/>.
        /// Gets how to specify the physical position and orientation on a mounted device.
        /// </summary>
        private UltraleapDeviceOffsetMode DeviceOffsetMode { get; }

        /// <summary>
        /// Gets the Ultraleap device's virtual X axis tilt.
        /// </summary>
        private float DeviceTiltXAxis { get; }

        /// <summary>
        /// Gets the Ultraleap device's virtual height offset from the tracked headset position.
        /// </summary>
        private float DeviceOffsetYAxis { get; }

        /// <summary>
        /// Gets the Ultraleap device's virtual depth offset from the tracked headset position.
        /// </summary>
        private float DeviceOffsetZAxis { get; }

        /// <summary>
        /// The current leap frame available.
        /// </summary>
        private Frame CurrentFrame
        {
            get
            {
                if (FrameOptimizationMode == UltraleapFrameOptimizationMode.ReusePhysicsForUpdate)
                {
                    return transformedFixedFrame;
                }

                return transformedUpdateFrame;
            }
        }

        /// <summary>
        /// The current leap fixed frame available.
        /// </summary>
        private Frame CurrentFixedFrame
        {
            get
            {
                if (FrameOptimizationMode == UltraleapFrameOptimizationMode.ReusePhysicsForUpdate)
                {
                    return transformedUpdateFrame;
                }

                return transformedFixedFrame;
            }
        }

        #region Mixed Reality Service Lifecycle

        /// <inheritdoc />
        public override void Enable()
        {
            base.Enable();

            CreateController();

            untransformedFixedFrame = new Frame();
            transformedFixedFrame = new Frame();
            untransformedUpdateFrame = new Frame();
            transformedUpdateFrame = new Frame();

            if (originConversionProxy.IsNull())
            {
                // We use a one conversion proxy for a hand's root position for either hand.
                // And we use one conversion proxy for any joint. These proxies are used
                // to transform hand/joint poses in coordinate spaces and so we're able to make
                // use of Unity's transform APIs. Most likely these proxies could be avoided if I
                // wasn't so bad at math.
                originConversionProxy = new GameObject("Ultraleap Origin Conversion Proxy");
                originConversionProxy.transform.SetParent(MixedRealityToolkit.CameraSystem.MainCameraRig.PlayspaceTransform);
                originConversionProxy.SetActive(false);
                handRootConversionProxy = new GameObject("Ultraleap Hand Root Conversion Proxy");
                handRootConversionProxy.transform.SetParent(originConversionProxy.transform);
                handRootConversionProxy.SetActive(false);
                jointConversionProxy = new GameObject("Ultraleap Hand Joint Conversion Proxy");
                jointConversionProxy.transform.SetParent(originConversionProxy.transform);
                jointConversionProxy.SetActive(false);
            }
        }

        /// <inheritdoc />
        public override void Update()
        {
            if (!CheckConnectionIntegrity())
            {
                return;
            }

            if (FrameOptimizationMode == UltraleapFrameOptimizationMode.ReusePhysicsForUpdate)
            {
                FrameReady(transformedFixedFrame);
                return;
            }

            ultraleapController.Frame(untransformedUpdateFrame);
            if (untransformedUpdateFrame != null)
            {
                TransformFrame(untransformedUpdateFrame, transformedUpdateFrame);
                FrameReady(transformedUpdateFrame);
            }
        }

        /// <inheritdoc />
        public override void FixedUpdate()
        {
            if (FrameOptimizationMode == UltraleapFrameOptimizationMode.ReuseUpdateForPhysics)
            {
                FrameReady(transformedUpdateFrame);
                return;
            }

            ultraleapController.Frame(untransformedFixedFrame);
            if (untransformedFixedFrame != null)
            {
                TransformFrame(untransformedFixedFrame, transformedFixedFrame);
                FrameReady(transformedFixedFrame);
            }
        }

        /// <inheritdoc />
        public override void Disable()
        {
            DestroyController();

            foreach (var activeController in activeControllers)
            {
                RemoveController(activeController.Key, false);
            }

            activeControllers.Clear();
            handIdMap.Clear();

            if (!originConversionProxy.IsNull())
            {
                originConversionProxy.Destroy();
            }
        }

        /// <inheritdoc />
        public override void Destroy()
        {
            DestroyController();
            base.Destroy();
        }

        #endregion Mixed Reality Service Lifecycle

        #region Ultraleap Controller Management

        private void CreateController()
        {
            if (ultraleapController != null)
            {
                return;
            }

            ultraleapController = new Controller();
            if (ultraleapController.IsConnected)
            {
                InitializeFlags();
            }
            else
            {
                ultraleapController.Device += UltraleapController_OnHandControllerConnect;
            }
        }

        private void DestroyController()
        {
            if (ultraleapController != null)
            {
                if (ultraleapController.IsConnected)
                {
                    ultraleapController.ClearPolicy(Controller.PolicyFlag.POLICY_OPTIMIZE_HMD);
                }

                ultraleapController.StopConnection();
                ultraleapController = null;
            }
        }

        private void UltraleapController_OnHandControllerConnect(object sender, DeviceEventArgs e)
        {
            InitializeFlags();

            if (ultraleapController != null)
            {
                ultraleapController.Device -= UltraleapController_OnHandControllerConnect;
            }
        }

        private void InitializeFlags()
        {
            if (ultraleapController == null)
            {
                return;
            }

            switch (OperationMode)
            {
                case UltraleapOperationMode.Desktop:
                    ultraleapController.SetPolicy(Controller.PolicyFlag.POLICY_DEFAULT);
                    break;
                case UltraleapOperationMode.HeadsetMounted:
                    ultraleapController.SetPolicy(Controller.PolicyFlag.POLICY_OPTIMIZE_HMD);
                    break;
            }
        }

        private bool CheckConnectionIntegrity()
        {
            if (ultraleapController.IsServiceConnected)
            {
                framesSinceServiceConnectionChecked = 0;
                numberOfReconnectionAttempts = 0;
                return true;
            }
            else if (numberOfReconnectionAttempts < MaxReconnectionAttempts)
            {
                framesSinceServiceConnectionChecked++;
                if (framesSinceServiceConnectionChecked > ReconnectionInterval)
                {
                    framesSinceServiceConnectionChecked = 0;
                    numberOfReconnectionAttempts++;

                    if (Debug.isDebugBuild)
                    {
                        Debug.LogWarning($"Ultraleap service not connected. Attempting to reconnect for try {numberOfReconnectionAttempts}/{MaxReconnectionAttempts}");
                    }
                }
            }

            return false;
        }

        private void TransformFrame(Frame source, Frame destination) => destination.CopyFrom(source).Transform(originConversionProxy.transform.GetLeapMatrix());

        private void FrameReady(Frame frame)
        {
            bool isLeftHandTracked = false;
            bool isRightHandTracked = false;

            Frame frame = e.frame;
            for (int i = 0; i < frame.Hands.Count; i++)
            {
                var hand = frame.Hands[i];

                if (hand.IsLeft && VerifyHandId(Handedness.Left, hand.Id) && TryGetHandData(hand, out var leftHandData))
                {
                    isLeftHandTracked = true;

                    var controller = GetOrAddController(Handedness.Left, hand.Id);
                    leftHandData = postProcessor.PostProcess(Handedness.Left, leftHandData);
                    controller?.UpdateController(leftHandData);
                }
                else if (hand.IsRight && VerifyHandId(Handedness.Right, hand.Id) && TryGetHandData(hand, out var rightHandData))
                {
                    isRightHandTracked = true;

                    var controller = GetOrAddController(Handedness.Right, hand.Id);
                    rightHandData = postProcessor.PostProcess(Handedness.Right, rightHandData);
                    controller?.UpdateController(rightHandData);
                }
            }

            if (!isLeftHandTracked)
            {
                RemoveController(Handedness.Left);
            }

            if (!isRightHandTracked)
            {
                RemoveController(Handedness.Right);
            }
        }

        #endregion Leap Controller Management

        #region Mixed Reality Hand Controller Instance Management

        private bool TryGetController(Handedness handedness, out MixedRealityHandController controller)
        {
            if (activeControllers.ContainsKey(handedness))
            {
                var existingController = activeControllers[handedness];
                Debug.Assert(existingController != null, $"Hand Controller {handedness} has been destroyed but remains in the active controller registry.");
                controller = existingController;
                return true;
            }

            controller = null;
            return false;
        }

        private MixedRealityHandController GetOrAddController(Handedness handedness, int handId)
        {
            // If a device is already registered with the handedness, just return it.
            if (TryGetController(handedness, out var existingController))
            {
                return existingController;
            }

            MixedRealityHandController detectedController;
            try
            {
                detectedController = new MixedRealityHandController(this, TrackingState.Tracked, handedness, GetControllerMappingProfile(typeof(MixedRealityHandController), handedness));
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create {nameof(MixedRealityHandController)}!\n{e}");
                return null;
            }

            detectedController.TryRenderControllerModel();
            AddController(detectedController);
            activeControllers.Add(handedness, detectedController);
            handIdMap.Add(handedness, handId);
            MixedRealityToolkit.InputSystem?.RaiseSourceDetected(detectedController.InputSource, detectedController);

            return detectedController;
        }

        private void RemoveController(Handedness handedness, bool removeFromRegistry = true)
        {
            if (TryGetController(handedness, out var controller))
            {
                MixedRealityToolkit.InputSystem?.RaiseSourceLost(controller.InputSource, controller);

                if (removeFromRegistry)
                {
                    RemoveController(controller);
                    activeControllers.Remove(handedness);
                    handIdMap.Remove(handedness);
                }
            }
        }

        private bool VerifyHandId(Handedness handedness, int handId)
        {
            if (handIdMap.ContainsKey(handedness))
            {
                // A hand ID for the provided ID is already
                // registered. We can only update the controller
                // for the handedness if the IDs match.
                return handIdMap[handedness] == handId;
            }

            // A hand ID for the provided handedness is currently
            // not registered at all. That means it's safe to create
            // a new controller for the provided ID.
            return true;
        }

        #endregion Mixed Reality Hand Controller Instance Management

        #region Hand Data Conversion

        /// <summary>
        /// Transforms platform provided hand tracking information to agnostic <see cref="HandData"/>.
        /// </summary>
        /// <param name="hand">The <see cref="Hand"/> tracking data provided by the platform.</param>
        /// <param name="handData">The output <see cref="HandData"/>.</param>
        /// <returns>True, if data conversion was a success.</returns>
        public bool TryGetHandData(Hand hand, out HandData handData)
        {
            if (hand == null)
            {
                handData = default;
                return false;
            }

            handData = new HandData
            {
                TrackingState = ultraleapController.IsConnected ? TrackingState.Tracked : TrackingState.NotTracked,
                UpdatedAt = DateTimeOffset.UtcNow.Ticks
            };

            if (handData.TrackingState == TrackingState.Tracked)
            {
                var deviceOffsetPose = GetCurrentDeviceOffsetPose();
                handData.RootPose = GetOriginPose(deviceOffsetPose);
                handData.Joints = GetJointPoses(hand);
                handData.PointerPose = GetPointerPose(handData.RootPose, handData.Joints);
                handData.Mesh = HandMeshData.Empty;
            }

            return true;
        }

        private MixedRealityPose[] GetJointPoses(Hand hand)
        {
            for (var i = 0; i < HandData.JointCount; i++)
            {
                var trackedHandJoint = (TrackedHandJoint)i;
                var position = Vector3.zero;
                var rotation = Quaternion.identity;

                switch (trackedHandJoint)
                {
                    case TrackedHandJoint.Wrist:
                        position = hand.WristPosition.ToLeftHandedUnityVector3();
                        rotation = hand.Arm.Basis.rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.Palm:
                        position = hand.PalmPosition.ToLeftHandedUnityVector3();
                        rotation = hand.Basis.rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.ThumbMetacarpal:
                        position = hand.Fingers[leapThumbIndex].bones[leapMetacarpalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapThumbIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.ThumbProximal:
                        position = hand.Fingers[leapThumbIndex].bones[leapProximalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapThumbIndex].bones[leapIntermediateBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.ThumbDistal:
                        position = hand.Fingers[leapThumbIndex].bones[leapIntermediateBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapThumbIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.ThumbTip:
                        position = hand.Fingers[leapThumbIndex].bones[leapDistalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapThumbIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.IndexProximal:
                        position = hand.Fingers[leapIndexFingerIndex].bones[leapMetacarpalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapIndexFingerIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.IndexIntermediate:
                        position = hand.Fingers[leapIndexFingerIndex].bones[leapProximalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapIndexFingerIndex].bones[leapIntermediateBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.IndexDistal:
                        position = hand.Fingers[leapIndexFingerIndex].bones[leapIntermediateBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapIndexFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.IndexTip:
                        position = hand.Fingers[leapIndexFingerIndex].bones[leapDistalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapIndexFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.MiddleProximal:
                        position = hand.Fingers[leapMiddleFingerIndex].bones[leapMetacarpalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapMiddleFingerIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.MiddleIntermediate:
                        position = hand.Fingers[leapMiddleFingerIndex].bones[leapProximalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapMiddleFingerIndex].bones[leapIntermediateBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.MiddleDistal:
                        position = hand.Fingers[leapMiddleFingerIndex].bones[leapIntermediateBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapMiddleFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.MiddleTip:
                        position = hand.Fingers[leapMiddleFingerIndex].bones[leapDistalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapMiddleFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.RingProximal:
                        position = hand.Fingers[leapRingFingerIndex].bones[leapMetacarpalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapRingFingerIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.RingIntermediate:
                        position = hand.Fingers[leapRingFingerIndex].bones[leapProximalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapRingFingerIndex].bones[leapIntermediateBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.RingDistal:
                        position = hand.Fingers[leapRingFingerIndex].bones[leapIntermediateBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapRingFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.RingTip:
                        position = hand.Fingers[leapRingFingerIndex].bones[leapDistalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapRingFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.LittleMetacarpal:
                        position = hand.Fingers[leapLittleFingerIndex].bones[leapMetacarpalBoneIndex].PrevJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapLittleFingerIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.LittleProximal:
                        position = hand.Fingers[leapLittleFingerIndex].bones[leapMetacarpalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapLittleFingerIndex].bones[leapProximalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.LittleIntermediate:
                        position = hand.Fingers[leapLittleFingerIndex].bones[leapProximalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapLittleFingerIndex].bones[leapIntermediateBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.LittleDistal:
                        position = hand.Fingers[leapLittleFingerIndex].bones[leapIntermediateBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapLittleFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                    case TrackedHandJoint.LittleTip:
                        position = hand.Fingers[leapLittleFingerIndex].bones[leapDistalBoneIndex].NextJoint.ToLeftHandedUnityVector3();
                        rotation = hand.Fingers[leapLittleFingerIndex].bones[leapDistalBoneIndex].Rotation.ToLeftHandedUnityQuaternion();
                        break;
                }

                jointConversionProxy.transform.localPosition = position;
                jointConversionProxy.transform.localRotation = rotation;

                jointPoses[i] = new MixedRealityPose(
                    jointConversionProxy.transform.localPosition,
                    jointConversionProxy.transform.localRotation);
            }

            // Fill missing joints by estimating their pose.
            jointPoses[(int)TrackedHandJoint.RingMetacarpal] = HandUtilities.GetEstimatedRingMetacarpalPose(jointPoses);
            jointPoses[(int)TrackedHandJoint.MiddleMetacarpal] = HandUtilities.GetEstimatedMiddleMetacarpalPose(jointPoses);
            jointPoses[(int)TrackedHandJoint.IndexMetacarpal] = HandUtilities.GetEstimatedIndexMetacarpalPose(jointPoses);

            return jointPoses;
        }

        /// <summary>
        /// Gets the <see cref="Hand"/>'s detected root pose. The root pose is the origin
        /// for all joint poses.
        /// </summary>
        /// <param name="hand">Platform provided <see cref="Hand"/> data.</param>
        /// <returns>The <see cref="Hand"/>'s root <see cref="MixedRealityPose"/>.</returns>
        private MixedRealityPose GetHandRootPose(Hand hand)
        {
            var position = hand.WristPosition.ToLeftHandedUnityVector3();
            var rotation = hand.Arm.Basis.rotation.ToLeftHandedUnityQuaternion();

            handRootConversionProxy.transform.localPosition = position;
            handRootConversionProxy.transform.localRotation = rotation;

            return new MixedRealityPose(handRootConversionProxy.transform.localPosition, handRootConversionProxy.transform.localRotation);
        }

        /// <summary>
        /// Gets the origin <see cref="MixedRealityPose"/> for the current frame. The origin is where the device is located at
        /// in playspace.
        /// </summary>
        /// <param name="deviceOffsetPose">The current device offset <see cref="MixedRealityPose"/> retrieved from <see cref="GetCurrentDeviceOffsetPose"/>.</param>
        /// <returns>Offset <see cref="MixedRealityPose"/>.</returns>
        private MixedRealityPose GetOriginPose(MixedRealityPose deviceOffsetPose)
        {
            var cameraTransform = MixedRealityToolkit.CameraSystem != null
                        ? MixedRealityToolkit.CameraSystem.MainCameraRig.PlayerCamera.transform
                        : CameraCache.Main.transform;

            var position = cameraTransform.localPosition + cameraTransform.localRotation * deviceOffsetPose.Position;
            var rotation = cameraTransform.localRotation * deviceOffsetPose.Rotation;

            originConversionProxy.transform.localPosition = position;
            originConversionProxy.transform.localRotation = rotation;

            return new MixedRealityPose(originConversionProxy.transform.localPosition = position, originConversionProxy.transform.localRotation = rotation);
        }

        /// <summary>
        /// Gets the device offset as configured in the active <see cref="UltraleapHandControllerDataProviderProfile"/>.
        /// </summary>
        /// <returns>Device offset <see cref="MixedRealityPose"/>.</returns>
        private MixedRealityPose GetCurrentDeviceOffsetPose()
        {
            switch (OperationMode)
            {
                case UltraleapOperationMode.Desktop:
                    return new MixedRealityPose(LeapControllerOffset, Quaternion.identity);
                case UltraleapOperationMode.HeadsetMounted:
                    switch (DeviceOffsetMode)
                    {
                        case UltraleapDeviceOffsetMode.Default:
                            return new MixedRealityPose(
                                new Vector3(0f, defaultDeviceOffsetYAxis, defaultDeviceOffsetZAxis),
                                Quaternion.Euler(defaultDeviceTiltXAxis, 0f, 0f));
                        case UltraleapDeviceOffsetMode.Manual:
                            return new MixedRealityPose(
                                new Vector3(0f, DeviceOffsetYAxis, DeviceOffsetZAxis),
                                Quaternion.Euler(DeviceTiltXAxis, 0f, 0f));
                    }
                    break;
            }

            throw new ArgumentException($"Ultraleap operation mode {OperationMode} with offset mode {DeviceOffsetMode} is not supported!");
        }

        private MixedRealityPose GetPointerPose(MixedRealityPose handRootPose, MixedRealityPose[] jointPoses)
        {
            var cameraTransform = MixedRealityToolkit.CameraSystem != null
                        ? MixedRealityToolkit.CameraSystem.MainCameraRig.PlayerCamera.transform
                        : CameraCache.Main.transform;
            var shoulderYaw = Quaternion.Euler(0f, cameraTransform.rotation.eulerAngles.y, 0f);
            var projectionOrigin = handRootPose.Position + cameraTransform.position + shoulderYaw * new Vector3(.15f, -.13f, .05f);
            var projectionDirection = jointPoses[(int)TrackedHandJoint.IndexProximal].Position - projectionOrigin;

            return new MixedRealityPose(handRootPose.Position + jointPoses[(int)TrackedHandJoint.IndexProximal].Position, Quaternion.LookRotation(handRootPose.Up, projectionDirection));
        }

        #endregion Hand Data Conversion
    }
}