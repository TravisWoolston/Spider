// using UnityEngine;
// using System.Collections;
// using System.Collections.Generic;
// using System.IO;

// [System.Serializable]
// public class SaveState
// {
//     public List<SpiderStateData> spiders = new List<SpiderStateData>();
//     public List<WebStringStateData> webs = new List<WebStringStateData>();
// }

// [System.Serializable]
// public class SpiderStateData
// {
//     public string name;
//     public string path;
//     public Vector2 position;
//     public float rotation;
//     public Vector2 velocity;
//     public Vector2 smoothedInput;
//     public float bodyHeight;
//     public List<LegStateData> legs = new List<LegStateData>();
// }

// [System.Serializable]
// public class LegStateData
// {
//     public bool isPlanted;
//     public Vector2 currentFootPos;
//     public string plantedPath;
//     public Vector2 plantedLocalPos;
// }

// [System.Serializable]
// public class WebStringStateData
// {
//     public string name;
//     public string parentPath;
//     public int stringId;
//     public List<WebSegmentStateData> segments = new List<WebSegmentStateData>();
// }

// [System.Serializable]
// public class WebSegmentStateData
// {
//     public Vector2 position;
//     public float rotation;
//     public Vector2 velocity;
//     public bool isAttached;
//     public bool firstSegment;
//     public ConnectionData startConnection; 
//     public ConnectionData endConnection;   
// }

// [System.Serializable]
// public class ConnectionData
// {
//     public bool enabled;
//     public string connectedPath; 
//     public Vector2 connectedAnchor;
// }

// public class SaveStateManager : MonoBehaviour
// {
//     public KeyCode saveKey = KeyCode.F5;
//     public KeyCode loadKey = KeyCode.F9;
    
//     private string savePath;
//     private SpiderWebShooter shooter;

//     void Start()
//     {
//         savePath = Path.Combine(Application.persistentDataPath, "savestate.json");
//         shooter = FindFirstObjectByType<SpiderWebShooter>();
//     }

//     void Update()
//     {
//         if (Input.GetKeyDown(saveKey)) Save();
//         if (Input.GetKeyDown(loadKey)) StartCoroutine(LoadCoroutine());
//     }

//     public void Save()
//     {
//         SaveState state = new SaveState();
//         var allSpiders = GameObject.FindObjectsByType<SpiderControllerIK>(FindObjectsSortMode.None);
//         foreach (var spider in allSpiders)
//         {
//             SpiderStateData sData = new SpiderStateData {
//                 name = spider.name,
//                 path = GetGameObjectPath(spider.gameObject),
//                 position = spider.headRb.position,
//                 rotation = spider.transform.eulerAngles.z,
//                 velocity = spider.headRb.linearVelocity,
//                 bodyHeight = spider.bodyHeight,
//                 smoothedInput = (Vector2)typeof(SpiderControllerIK).GetField("smoothedInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(spider)
//             };
//             foreach (var leg in spider.legs) sData.legs.Add(new LegStateData {
//                 isPlanted = leg.isPlanted,
//                 currentFootPos = leg.currentFootPos,
//                 plantedPath = leg.plantedTransform != null ? GetGameObjectPath(leg.plantedTransform.gameObject) : "",
//                 plantedLocalPos = leg.plantedLocalPos
//             });
//             state.spiders.Add(sData);
//         }

//         var webContainers = GameObject.FindObjectsByType<WebStringRenderer>(FindObjectsSortMode.None);
//         foreach (var container in webContainers)
//         {
//             WebStringStateData webData = new WebStringStateData {
//                 name = container.name,
//                 parentPath = container.transform.parent != null ? GetGameObjectPath(container.transform.parent.gameObject) : ""
//             };
//             var firstSeg = container.GetComponentInChildren<WebSegment>();
//             if (firstSeg != null) webData.stringId = firstSeg.stringId;
//             foreach (Transform child in container.transform)
//             {
//                 WebSegment seg = child.GetComponent<WebSegment>();
//                 if (seg == null) continue;
//                 webData.segments.Add(new WebSegmentStateData {
//                     position = seg.transform.position,
//                     rotation = seg.transform.eulerAngles.z,
//                     velocity = seg.GetComponent<Rigidbody2D>().linearVelocity,
//                     isAttached = seg.isAttached,
//                     firstSegment = seg.firstSegment,
//                     startConnection = CaptureConnection(seg.hinge2),
//                     endConnection = CaptureConnection(seg.hinge)
//                 });
//             }
//             state.webs.Add(webData);
//         }
//         File.WriteAllText(savePath, JsonUtility.ToJson(state, true));
//         Debug.Log("Scene State Snapshotted!");
//     }

//     private ConnectionData CaptureConnection(HingeJoint2D hinge)
//     {
//         return new ConnectionData {
//             enabled = hinge.enabled,
//             connectedPath = (hinge.enabled && hinge.connectedBody != null) ? GetGameObjectPath(hinge.connectedBody.gameObject) : "",
//             connectedAnchor = hinge.connectedAnchor
//         };
//     }

//     IEnumerator LoadCoroutine()
//     {
//         if (!File.Exists(savePath)) yield break;
//         SaveState state = JsonUtility.FromJson<SaveState>(File.ReadAllText(savePath));

//         // 1. SUPPRESSION PHASE: Stop all physics and logic
//         Physics2D.simulationMode = SimulationMode2D.Script;
//         var allSpiders = GameObject.FindObjectsByType<SpiderControllerIK>(FindObjectsSortMode.None);
//         foreach (var s in allSpiders) s.enabled = false;

//         var oldWebs = GameObject.FindObjectsByType<WebStringRenderer>(FindObjectsSortMode.None);
//         foreach (var w in oldWebs) DestroyImmediate(w.gameObject);

//         // 2. RECONSTRUCTION PHASE: Spawn hulls
//         Dictionary<string, GameObject> containerMap = new Dictionary<string, GameObject>();
//         foreach (var webData in state.webs)
//         {
//             GameObject container = new GameObject(webData.name);
//             container.AddComponent<WebStringRenderer>().enabled = false; 
//             containerMap[webData.name] = container;
//         }

//         foreach (var webData in state.webs)
//         {
//             GameObject container = containerMap[webData.name];
//             for (int i = 0; i < webData.segments.Count; i++)
//             {
//                 var sData = webData.segments[i];
//                 GameObject segGO = Instantiate(shooter.webPrefab, sData.position, Quaternion.Euler(0, 0, sData.rotation), container.transform);
//                 segGO.name = "Segment_" + i;
//                 Rigidbody2D rb = segGO.GetComponent<Rigidbody2D>();
//                 rb.bodyType = RigidbodyType2D.Dynamic;
//                 rb.mass = 0.1f; rb.linearDamping = 2f; rb.angularDamping = 2f; rb.gravityScale = 0.5f;
//                 rb.linearVelocity = sData.velocity;
//                 WebSegment seg = segGO.GetComponent<WebSegment>();
//                 seg.stringId = webData.stringId; seg.isAttached = sData.isAttached; seg.firstSegment = sData.firstSegment;
//             }
//         }

//         // Wait for Unity to register transforms
//         Physics2D.SyncTransforms();
//         yield return null; 

//         // 3. WIRING PHASE: Link joints and re-parent
//         foreach (var webData in state.webs)
//         {
//             if (!string.IsNullOrEmpty(webData.parentPath))
//             {
//                 GameObject p = FindGameObjectByPath(webData.parentPath);
//                 if (p != null) containerMap[webData.name].transform.SetParent(p.transform);
//             }
//         }

//         var allSegs = GameObject.FindObjectsByType<WebSegment>(FindObjectsSortMode.None);
//         foreach (var seg in allSegs)
//         {
//             string path = GetGameObjectPath(seg.gameObject);
//             WebSegmentStateData data = FindDataForPath(state, path);
//             if (data != null)
//             {
//                 RestoreJoint(seg.hinge2, data.startConnection);
//                 RestoreJoint(seg.hinge, data.endConnection);
//                 if (seg.hinge.enabled && seg.hinge.connectedBody != null)
//                 {
//                     WebSegment other = seg.hinge.connectedBody.GetComponent<WebSegment>();
//                     if (other != null) seg.RegisterNeighbor(other);
//                 }
//                 if (seg.hinge2.enabled && seg.hinge2.connectedBody != null)
//                 {
//                     WebSegment other = seg.hinge2.connectedBody.GetComponent<WebSegment>();
//                     if (other != null) seg.RegisterNeighbor(other);
//                 }
//             }
//         }

//         // 4. SETTLING PHASE: Teleport Spiders
//         foreach (var sData in state.spiders)
//         {
//             GameObject sGO = FindGameObjectByPath(sData.path) ?? GameObject.Find(sData.name);
//             if (sGO != null)
//             {
//                 SpiderControllerIK spider = sGO.GetComponent<SpiderControllerIK>();
//                 spider.headRb.linearVelocity = sData.velocity;
//                 spider.headRb.angularVelocity = 0f;
//                 spider.transform.position = sData.position;
//                 spider.transform.rotation = Quaternion.Euler(0, 0, sData.rotation);
//                 spider.headRb.position = sData.position;
                
//                 spider.bodyHeight = sData.bodyHeight;
//                 typeof(SpiderControllerIK).GetField("smoothedInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(spider, sData.smoothedInput);
//                 typeof(SpiderControllerIK).GetField("targetBodyHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(spider, sData.bodyHeight);

//                 for (int i = 0; i < spider.legs.Length; i++)
//                 {
//                     if (i >= sData.legs.Count) break;
//                     var leg = spider.legs[i]; var lData = sData.legs[i];
//                     leg.TeleportFoot(lData.currentFootPos, lData.isPlanted);
//                     leg.plantedLocalPos = lData.plantedLocalPos;
//                     if (!string.IsNullOrEmpty(lData.plantedPath))
//                     {
//                         GameObject target = FindGameObjectByPath(lData.plantedPath);
//                         if (target != null) { leg.plantedTransform = target.transform; leg.plantedRb = target.GetComponent<Rigidbody2D>(); }
//                     }
//                 }
//                 spider.enabled = true;
//                 spider.headRb.WakeUp();
//             }
//         }

//         // Enable renderers
//         foreach(var map in containerMap.Values) map.GetComponent<WebStringRenderer>().enabled = true;

//         Physics2D.SyncTransforms();
//         Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
//         Debug.Log("Save State Loaded (Complete Scene Synchronization)");
//     }

//     private void RestoreJoint(HingeJoint2D hinge, ConnectionData data)
//     {
//         hinge.enabled = data.enabled;
//         if (!data.enabled) return;
//         if (string.IsNullOrEmpty(data.connectedPath)) { hinge.connectedBody = null; }
//         else {
//             GameObject target = FindGameObjectByPath(data.connectedPath);
//             if (target != null) hinge.connectedBody = target.GetComponent<Rigidbody2D>();
//         }
//         hinge.connectedAnchor = data.connectedAnchor;
//     }

//     private WebSegmentStateData FindDataForPath(SaveState state, string path)
//     {
//         foreach (var web in state.webs)
//         {
//             for (int i = 0; i < web.segments.Count; i++){
//                 if (path.EndsWith("/" + web.name + "/Segment_" + i)) return web.segments[i];
//             }
//         }
//         return null;
//     }

//     private GameObject FindGameObjectByPath(string path)
//     {
//         if (string.IsNullOrEmpty(path)) return null;
//         GameObject obj = GameObject.Find(path);
//         if (obj != null) return obj;
//         string[] parts = path.Split('/');
//         string targetName = parts[parts.Length - 1];
//         if (targetName.StartsWith("Segment_") && parts.Length >= 2)
//         {
//             GameObject container = GameObject.Find(parts[parts.Length - 2]);
//             if (container != null) { foreach (Transform child in container.transform) if (child.name == targetName) return child.gameObject; }
//         }
//         return GameObject.Find(targetName);
//     }

//     private string GetGameObjectPath(GameObject obj)
//     {
//         string path = "/" + obj.name;
//         while (obj.transform.parent != null) { obj = obj.transform.parent.gameObject; path = "/" + obj.name + path; }
//         return path;
//     }
// }
