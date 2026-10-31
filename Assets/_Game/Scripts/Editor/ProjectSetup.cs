using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Inkwave.EditorTools
{
    /// <summary>
    /// One-shot project configuration (player settings for the Android build). Idempotent; run from the menu or
    /// Unity -batchmode -projectPath . -executeMethod Inkwave.EditorTools.ProjectSetup.Apply -quit
    /// </summary>
    public static class ProjectSetup
    {
        public const string CompanyName = "Zoiko";
        public const string ProductName = "Color Splash";
        public const string PackageId = "com.zoiko.colorsplash";

        [MenuItem("Inkwave/Apply Project Settings")]
        public static void Apply()
        {
            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, PackageId);
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Standalone, PackageId);
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // landscape only, either way up (INKWAVE's portrait gate becomes a hard orientation lock on a phone)
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)26;
            PlayerSettings.Android.forceInternetPermission = false;   // offline build: never asks for the network

            // Input System only (touch, keyboard/mouse and gamepad all come through it)
            var player = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            SerializedProperty handler = player.FindProperty("activeInputHandler");
            if (handler != null) handler.intValue = 1;
            player.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();
            Debug.Log("[Inkwave] Project settings applied");
        }
    }
}
