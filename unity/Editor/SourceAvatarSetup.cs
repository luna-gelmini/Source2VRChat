using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if VRC_SDK_VRCSDK3
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace Source2VRChat
{
    /// <summary>
    /// Finishes an avatar converted with gma2blend: humanoid import settings, materials,
    /// eye look, helper-bone constraints, bone order and PhysBone collider placement.
    /// </summary>
    public class SourceAvatarSetup : EditorWindow
    {
        const float EyeUpDown = 3f, EyeLeftRight = 4f;

        GameObject avatar;
        TextAsset report;
        bool doImport = true, doMaterials = true, doEyes = true, doHelpers = true, doBoneOrder = true, doColliders = true;
        Vector2 scroll;
        readonly List<string> log = new List<string>();

        [MenuItem("Tools/Source Avatar Setup")]
        static void Open() => GetWindow<SourceAvatarSetup>("Source Avatar Setup");

        void OnGUI()
        {
            avatar = (GameObject)EditorGUILayout.ObjectField("Avatar (scene)", avatar, typeof(GameObject), true);
            report = (TextAsset)EditorGUILayout.ObjectField("report.json", report, typeof(TextAsset), false);
            EditorGUILayout.Space();
            doImport = EditorGUILayout.ToggleLeft("FBX import settings (Humanoid, no Jaw on hair)", doImport);
            doMaterials = EditorGUILayout.ToggleLeft("Create and assign materials", doMaterials);
            doEyes = EditorGUILayout.ToggleLeft("Eye look rotations", doEyes);
            doHelpers = EditorGUILayout.ToggleLeft("Constraints for Source helper bones", doHelpers);
            doBoneOrder = EditorGUILayout.ToggleLeft("Hand/foot bones first in hierarchy", doBoneOrder);
            doColliders = EditorGUILayout.ToggleLeft("Move colliders out of PhysBone roots", doColliders);

            using (new EditorGUI.DisabledScope(avatar == null || (doMaterials && report == null)))
                if (GUILayout.Button("Run", GUILayout.Height(28)))
                    Run();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var line in log)
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        void Run()
        {
            if (EditorApplication.isPlaying)
            {
                log.Add("Exit play mode first.");
                return;
            }
            log.Clear();
            var data = report ? JsonUtility.FromJson<Report>(report.text) : null;
            try
            {
                if (doImport) FixImport();
                if ((doHelpers || doBoneOrder || doColliders) && PrefabUtility.IsPartOfPrefabInstance(avatar))
                {
                    // Bones inside a model prefab instance can't be reparented or reordered.
                    PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(avatar),
                        PrefabUnpackMode.Completely, InteractionMode.UserAction);
                    log.Add("Unpacked the model prefab so bones can be edited.");
                }
                var animator = avatar.GetComponent<Animator>();
                if (doMaterials) AssignMaterials(data);
                if (doBoneOrder) FixBoneOrder(animator);
#if VRC_SDK_VRCSDK3
                if (doEyes) FixEyeLook(animator);
                if (doHelpers) ConstrainHelpers(animator);
                if (doColliders) FixColliders();
#else
                if (doEyes || doHelpers || doColliders) log.Add("VRChat Avatars SDK not found: skipped eyes, helpers and colliders.");
#endif
            }
            catch (Exception e)
            {
                log.Add("Failed: " + e.Message);
                Debug.LogException(e);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);
            log.Add("Done. Save the scene to keep the changes.");
        }


        void FixImport()
        {
            var mesh = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.sharedMesh)?.sharedMesh;
            var path = mesh ? AssetDatabase.GetAssetPath(mesh) : null;
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
            {
                log.Add("Import: no model file found behind the avatar's meshes.");
                return;
            }
            importer.animationType = ModelImporterAnimationType.Human;
            importer.importBlendShapeNormals = ModelImporterNormals.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();

            // The auto-mapping only exists after the humanoid import above, so it's corrected in a second pass.
            var description = importer.humanDescription;
            var changed = CorrectMapping(ref description);
            if (changed > 0)
            {
                importer.humanDescription = description;
                importer.SaveAndReimport();
            }

            var animator = avatar.GetComponent<Animator>();
            if (animator)
            {
                animator.avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
                animator.Rebind();
            }
            log.Add($"Import: Humanoid set on {Path.GetFileName(path)}" + (changed > 0 ? $", {changed} bone mappings corrected." : "."));
        }

        /// <summary>
        /// gma2blend names humanoid bones exactly, so those names win. Optional slots the auto-mapper
        /// filled with unrelated bones (hair as Jaw, physics chains as fingers or eyes) are cleared.
        /// </summary>
        int CorrectMapping(ref HumanDescription description)
        {
            var bones = new HashSet<string>(avatar.GetComponentsInChildren<Transform>(true).Select(t => t.name));
            var mapping = description.human.ToDictionary(b => b.humanName, b => b.boneName);
            int changed = 0;
            for (int i = 0; i < HumanTrait.BoneCount; i++)
            {
                var humanName = HumanTrait.BoneName[i];
                var exact = humanName.Replace(" ", "");
                if (bones.Contains(exact))
                {
                    if (!mapping.TryGetValue(humanName, out var current) || current != exact)
                    {
                        mapping[humanName] = exact;
                        changed++;
                    }
                }
                else if (!HumanTrait.RequiredBone(i) && mapping.TryGetValue(humanName, out var bone) && !Resembles(humanName, bone))
                {
                    mapping.Remove(humanName);
                    changed++;
                }
            }
            description.human = mapping.Select(m => new HumanBone
            {
                humanName = m.Key, boneName = m.Value, limit = new HumanLimit { useDefaultValues = true },
            }).ToArray();
            return changed;
        }

        static readonly (string slot, string[] words)[] SlotWords =
        {
            ("Thumb", new[] { "thumb", "finger0" }), ("Index", new[] { "index", "finger1" }),
            ("Middle", new[] { "middle", "finger2" }), ("Ring", new[] { "ring", "finger3" }),
            ("Little", new[] { "little", "pinky", "finger4" }), ("Eye", new[] { "eye" }), ("Jaw", new[] { "jaw" }),
            ("Toes", new[] { "toe" }), ("Shoulder", new[] { "shoulder", "clavicle", "collar" }),
            ("Chest", new[] { "chest", "spine" }), ("Neck", new[] { "neck" }),
        };

        static bool Resembles(string humanName, string boneName)
        {
            var bone = boneName.ToLowerInvariant();
            foreach (var (slot, words) in SlotWords)
                if (humanName.Contains(slot))
                    return words.Any(bone.Contains);
            return true;
        }


        void AssignMaterials(Report data)
        {
            var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(report)).Replace('\\', '/');
            var materialFolder = folder + "/Materials";
            if (!AssetDatabase.IsValidFolder(materialFolder))
                AssetDatabase.CreateFolder(folder, "Materials");

            var created = new Dictionary<string, Material>();
            int assigned = 0, uncertain = 0;
            // Addons often ship several models with identically named meshes; prefer the one this FBX came from.
            var fbx = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh).Where(m => m)
                .Select(m => Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(m))).FirstOrDefault();
            var models = data.models.Where(m => m.model == fbx).DefaultIfEmpty().ToArray();
            if (models[0] == null) models = data.models;
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var source = models.SelectMany(m => m.meshes).FirstOrDefault(m => m.name == renderer.name);
                if (source == null || source.materials.Length == 0 || !renderer.sharedMesh) continue;

                var mesh = renderer.sharedMesh;
                var used = new bool[source.materials.Length];
                var slots = new Material[mesh.subMeshCount];
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    // Unity may reorder submeshes and drops degenerate triangles, so match each
                    // submesh to the unused Blender material with the closest triangle count.
                    int tris = (int)(mesh.GetSubMesh(sub).indexCount / 3);
                    int match = Enumerable.Range(0, used.Length).Where(i => !used[i])
                        .OrderBy(i => Math.Abs(source.material_tris[i] - tris)).DefaultIfEmpty(-1).First();
                    if (match < 0)
                        match = Math.Min(sub, used.Length - 1);
                    else if (used.Length > 1 && Math.Abs(source.material_tris[match] - tris) > Math.Max(2, tris / 50))
                        uncertain++;
                    used[match] = true;
                    slots[sub] = GetMaterial(source.materials[match], data, folder, materialFolder, created);
                }
                renderer.sharedMaterials = slots;
                assigned++;
            }
            log.Add($"Materials: {created.Count} created, {assigned} renderers assigned" +
                    (uncertain > 0 ? $", {uncertain} submeshes matched approximately (check them)." : "."));
        }

        static Material GetMaterial(string name, Report data, string folder, string materialFolder, Dictionary<string, Material> cache)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            var path = $"{materialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material)
            {
                var info = data.materials.FirstOrDefault(m => m.name == name.ToLowerInvariant()) ?? new VmtInfo { texture = name };
                material = new Material(PickShader(info)) { name = name };
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/Textures/{info.texture}.png");
                if (texture) material.mainTexture = texture;
                if (info.double_sided && material.HasProperty("_Cull")) material.SetFloat("_Cull", 0);
                AssetDatabase.CreateAsset(material, path);
            }
            cache[name] = material;
            return material;
        }

        static Shader PickShader(VmtInfo info)
        {
            if (Shader.Find("lilToon"))
                return Shader.Find(info.translucent ? "Hidden/lilToonTransparent" : info.alphatest ? "Hidden/lilToonCutout" : "lilToon");
            return Shader.Find("Standard");
        }


        void FixBoneOrder(Animator animator)
        {
            // VRChat expects the next limb bone to be the first child.
            var bones = new[]
            {
                HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            };
            int moved = 0;
            foreach (var bone in bones)
            {
                var t = animator ? animator.GetBoneTransform(bone) : null;
                if (t && t.GetSiblingIndex() != 0)
                {
                    t.SetSiblingIndex(0);
                    moved++;
                }
            }
            log.Add($"Bone order: {moved} bones moved first.");
        }

#if VRC_SDK_VRCSDK3
        void FixEyeLook(Animator animator)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var left = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            var right = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (!descriptor)
            {
                log.Add("Eyes: add a VRC Avatar Descriptor first.");
                return;
            }
            if (!left || !right)
            {
                log.Add("Eyes: no eye bones, skipped (eyes are probably part of the face texture).");
                return;
            }

            // Blank rotations snap Source eye bones to identity and roll the pupils out of sight.
            VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations Look(Quaternion world) =>
                new VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations
                {
                    linked = true,
                    left = Quaternion.Inverse(left.parent.rotation) * world * left.rotation,
                    right = Quaternion.Inverse(right.parent.rotation) * world * right.rotation,
                };

            var settings = descriptor.customEyeLookSettings;
            var side = avatar.transform.right;
            var up = avatar.transform.up;
            settings.leftEye = left;
            settings.rightEye = right;
            settings.eyesLookingStraight = Look(Quaternion.identity);
            settings.eyesLookingUp = Look(Quaternion.AngleAxis(-EyeUpDown, side));
            settings.eyesLookingDown = Look(Quaternion.AngleAxis(EyeUpDown, side));
            settings.eyesLookingLeft = Look(Quaternion.AngleAxis(-EyeLeftRight, up));
            settings.eyesLookingRight = Look(Quaternion.AngleAxis(EyeLeftRight, up));
            descriptor.enableEyeLook = true;
            descriptor.customEyeLookSettings = settings;
            EditorUtility.SetDirty(descriptor);
            log.Add($"Eyes: look rotations set ({EyeUpDown}° up/down, {EyeLeftRight}° left/right).");
        }

        void ConstrainHelpers(Animator animator)
        {
            var limbs = new Dictionary<Transform, HumanBodyBones>();
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
                if (b != HumanBodyBones.LastBone && animator.GetBoneTransform(b) is Transform t)
                    limbs[t] = b;

            int count = 0;
            foreach (var bone in avatar.GetComponentsInChildren<Transform>(true).ToArray())
            {
                if (limbs.ContainsKey(bone) || !bone.parent || !limbs.TryGetValue(bone.parent, out var parentBone))
                    continue;
                if (bone.GetComponent<VRCRotationConstraint>() || bone.parent.name.EndsWith("_Driver"))
                    continue;
                var name = bone.name.ToLowerInvariant();

                if (name.Contains("twist") && (parentBone == HumanBodyBones.LeftLowerArm || parentBone == HumanBodyBones.RightLowerArm))
                {
                    var hand = animator.GetBoneTransform(parentBone == HumanBodyBones.LeftLowerArm ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    AddTwistDriver(bone, hand);
                    count++;
                }
                else if (name.Contains("_root") || name.Contains("elbow") || name.Contains("knee"))
                {
                    // Volume helpers sit halfway between the joint's two bones.
                    var above = UpperNeighbour(animator, parentBone);
                    if (!above) continue;
                    AddBlend(bone, above, bone.parent);
                    count++;
                }
            }
            log.Add($"Helpers: {count} constraints added.");
        }

        static Transform UpperNeighbour(Animator animator, HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.LeftLowerArm: return animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                case HumanBodyBones.RightLowerArm: return animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                case HumanBodyBones.LeftLowerLeg: return animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                case HumanBodyBones.RightLowerLeg: return animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.RightUpperLeg: return animator.GetBoneTransform(HumanBodyBones.Hips);
                default: return null;
            }
        }

        static void AddBlend(Transform target, Transform a, Transform b)
        {
            var constraint = Undo.AddComponent<VRCRotationConstraint>(target.gameObject);
            AddSources(constraint, a, b);
        }

        /// <summary>
        /// Equal-weight sources with the offset baked by the SDK from the current (rest) pose,
        /// so the bone keeps its rest rotation until the limb moves.
        /// </summary>
        static void AddSources(VRCRotationConstraint constraint, Transform a, Transform b)
        {
            foreach (var source in new[] { a, b })
                constraint.Sources.Add(new VRCConstraintSource(source, 0.5f, Vector3.zero, Vector3.zero));
            constraint.ActivateConstraint();
            constraint.Locked = true;
            constraint.IsActive = true;
        }

        /// <summary>
        /// Axis filters only behave on a transform with identity rest rotation, so the twist bone
        /// gets an aligned parent that copies half of the hand's roll around the forearm.
        /// </summary>
        static void AddTwistDriver(Transform twist, Transform hand)
        {
            var forearm = twist.parent;
            var driver = new GameObject(twist.name + "_Driver").transform;
            Undo.RegisterCreatedObjectUndo(driver.gameObject, "Twist driver");
            driver.SetParent(forearm, false);
            driver.position = twist.position;
            Undo.SetTransformParent(twist, driver, "Twist driver");

            var axis = forearm.InverseTransformDirection(hand.position - forearm.position);
            var abs = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            var constraint = Undo.AddComponent<VRCRotationConstraint>(driver.gameObject);
            constraint.AffectsRotationX = abs.x >= abs.y && abs.x >= abs.z;
            constraint.AffectsRotationY = !constraint.AffectsRotationX && abs.y >= abs.z;
            constraint.AffectsRotationZ = !constraint.AffectsRotationX && !constraint.AffectsRotationY;
            AddSources(constraint, forearm, hand);
        }

        void FixColliders()
        {
            // A collider inside the bones its own PhysBone moves creates a cyclic dependency.
            int moved = 0;
            foreach (var physBone in avatar.GetComponentsInChildren<VRCPhysBone>(true))
            {
                var root = physBone.rootTransform ? physBone.rootTransform : physBone.transform;
                foreach (var collider in physBone.colliders.OfType<VRCPhysBoneCollider>())
                {
                    if (collider && collider.transform.IsChildOf(root) && root.parent)
                    {
                        Undo.SetTransformParent(collider.transform, root.parent, "Move collider");
                        moved++;
                    }
                }
            }
            log.Add($"Colliders: {moved} moved out of PhysBone roots.");
        }
#endif


        [Serializable] class Report { public ModelInfo[] models; public VmtInfo[] materials; }
        [Serializable] class ModelInfo { public string model; public MeshInfo[] meshes; }
        [Serializable] class MeshInfo { public string name; public string[] materials; public int[] material_tris; }
        [Serializable] class VmtInfo { public string name; public string texture; public bool translucent, alphatest, double_sided; }
    }
}
