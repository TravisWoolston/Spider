// C# script (Unity)
using UnityEngine;
using System.Collections;
public class ParallaxLayer : MonoBehaviour
{
    public float parallaxMultiplier = 0.5f;
    private Vector3 lastCamPos;
    private Transform cam;

    void Start()
    {
        cam = Camera.main.transform;
        lastCamPos = cam.position;
    }

    void LateUpdate()
    {
        Vector3 delta = cam.position - lastCamPos;
        transform.position += new Vector3(delta.x * parallaxMultiplier, delta.y * parallaxMultiplier, 0);
        lastCamPos = cam.position;
    }
}