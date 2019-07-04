using UnityEngine;
using HoloLensCameraStream;
using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;



/// <summary>
/// In this example, we back-project to the 3D world 5 pixels, which are the principal point and the image corners,
/// using the extrinsic parameters and projection matrices.
/// Whereas the app is running, if you tap on the image, this set of points is reprojected into the world.
/// </summary>
public class ProjectionExample : MonoBehaviour {

    private string path;
    


    // "Injected materials"
    public Material _topLeftMaterial;
    public Material _topRightMaterial;
    public Material _botLeftMaterial;
    public Material _botRightMaterial;
    public Material _centerMaterial;

    private HoloLensCameraStream.Resolution _resolution;
    private VideoCapture _videoCapture;
    private IntPtr _spatialCoordinateSystemPtr;
    private byte[] _latestImageBytes;
    private bool stopVideo;
    private UnityEngine.XR.WSA.Input.GestureRecognizer _gestureRecognizer;

    // Frame gameobject, renderer and texture
    private GameObject _picture;
    private Renderer _pictureRenderer;
    private Texture2D _pictureTexture;

    private RaycastLaser _laser;

    // This struct store frame related data
    private class SampleStruct
    {
        public float[] camera2WorldMatrix, projectionMatrix;
        public byte[] data;
    }

    void Awake()
    {
        // Create and set the gesture recognizer
        _gestureRecognizer = new UnityEngine.XR.WSA.Input.GestureRecognizer();
        _gestureRecognizer.TappedEvent += (source, tapCount, headRay) => { Debug.Log("Tapped"); StartCoroutine(StopVideoMode()); };
        _gestureRecognizer.SetRecognizableGestures(UnityEngine.XR.WSA.Input.GestureSettings.Tap);
        _gestureRecognizer.StartCapturingGestures();
    }

	void Start() 
    {
        //Fetch a pointer to Unity's spatial coordinate system if you need pixel mapping
        _spatialCoordinateSystemPtr = UnityEngine.XR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr();
	    CameraStreamHelper.Instance.GetVideoCaptureAsync(OnVideoCaptureCreated);

        // Create the frame container and apply HolographicImageBlend shader
        _picture = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _pictureRenderer = _picture.GetComponent<Renderer>() as Renderer;
        _pictureRenderer.material = new Material(Shader.Find("AR/HolographicImageBlend"));

        // Set the laser
        _laser = GetComponent<RaycastLaser>();

        // Create 3D file
        path = Path.Combine(Application.persistentDataPath, "screen.obj");
        File.WriteAllText(path, "");


    }

    // This coroutine will toggle the video on/off
    private IEnumerator StopVideoMode()
    {
        yield return new WaitForSeconds(3.0f);
        stopVideo = !stopVideo;

        if(!stopVideo)
            OnVideoCaptureCreated(_videoCapture);
    }

    private void OnDestroy()
    {
        if(_videoCapture == null)
            return;

        _videoCapture.FrameSampleAcquired += null;
        _videoCapture.Dispose();
    }

    private void OnVideoCaptureCreated(VideoCapture v)
    {
        if(v == null)
        {
            Debug.LogError("No VideoCapture found");
            return;
        }

        _videoCapture = v;

        //Request the spatial coordinate ptr if you want fetch the camera and set it if you need to 
        CameraStreamHelper.Instance.SetNativeISpatialCoordinateSystemPtr(_spatialCoordinateSystemPtr);

        _resolution = CameraStreamHelper.Instance.GetLowestResolution();
        float frameRate = CameraStreamHelper.Instance.GetHighestFrameRate(_resolution);

        _videoCapture.FrameSampleAcquired += OnFrameSampleAcquired;

        CameraParameters cameraParams = new CameraParameters();
        cameraParams.cameraResolutionHeight = _resolution.height;
        cameraParams.cameraResolutionWidth = _resolution.width;
        cameraParams.frameRate = Mathf.RoundToInt(frameRate);
        cameraParams.pixelFormat = CapturePixelFormat.BGRA32;

        UnityEngine.WSA.Application.InvokeOnAppThread(() => { _pictureTexture = new Texture2D(_resolution.width, _resolution.height, TextureFormat.BGRA32, false); }, false);

        _videoCapture.StartVideoModeAsync(cameraParams, OnVideoModeStarted);
    }

    private void OnVideoModeStarted(VideoCaptureResult result)
    {
        if(result.success == false)
        {
            Debug.LogWarning("Could not start video mode.");
            return;
        }

        Debug.Log("Video capture started.");
    }

    private void OnFrameSampleAcquired(VideoCaptureSample sample)
    {
        // Allocate byteBuffer
        if(_latestImageBytes == null || _latestImageBytes.Length < sample.dataLength)
            _latestImageBytes = new byte[sample.dataLength];

        // Fill frame struct 
        SampleStruct s = new SampleStruct();
        sample.CopyRawImageDataIntoBuffer(_latestImageBytes);
        s.data = _latestImageBytes;

        // Get the cameraToWorldMatrix and projectionMatrix
        if(!sample.TryGetCameraToWorldMatrix(out s.camera2WorldMatrix) || !sample.TryGetProjectionMatrix(out s.projectionMatrix))
            return;

        

        Matrix4x4 camera2WorldMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(s.camera2WorldMatrix);
        Matrix4x4 projectionMatrix = LocatableCameraUtils.ConvertFloatArrayToMatrix4x4(s.projectionMatrix);

        
        UnityEngine.WSA.Application.InvokeOnAppThread(() =>
        {
            // Upload bytes to texture
            _pictureTexture.LoadRawTextureData(s.data);
            _pictureTexture.wrapMode = TextureWrapMode.Clamp;
            _pictureTexture.Apply();

            // Set material parameters
            _pictureRenderer.sharedMaterial.SetTexture("_MainTex", _pictureTexture);
            _pictureRenderer.sharedMaterial.SetMatrix("_WorldToCameraMatrix", camera2WorldMatrix.inverse);
            _pictureRenderer.sharedMaterial.SetMatrix("_CameraProjectionMatrix", projectionMatrix);
            _pictureRenderer.sharedMaterial.SetFloat("_VignetteScale", 0f);

            Vector3 inverseNormal = -camera2WorldMatrix.GetColumn(2);
            // Position the canvas object slightly in front of the real world web camera.
            Vector3 imagePosition = camera2WorldMatrix.GetColumn(3) - camera2WorldMatrix.GetColumn(2);

            _picture.transform.position = imagePosition;
            _picture.transform.rotation = Quaternion.LookRotation(inverseNormal, camera2WorldMatrix.GetColumn(1));

        }, false);

        // Stop the video and reproject the 5 pixels
        if(stopVideo)
        {
            

            _videoCapture.StopVideoModeAsync(onVideoModeStopped);

            // Get the ray directions
            Vector3 imageCenterDirection = LocatableCameraUtils.PixelCoordToWorldCoord(camera2WorldMatrix, projectionMatrix, _resolution, new Vector2(_resolution.width/2, _resolution.height / 2));

            UnityEngine.WSA.Application.InvokeOnAppThread(() =>
            {
                // Paint the rays on the 3d world
                _laser.shootLaserFrom(camera2WorldMatrix.GetColumn(3), imageCenterDirection, 10f, _centerMaterial);

                _pictureTexture.LoadRawTextureData(s.data);
                _pictureTexture.Apply();

                //Add points in the center of the image
                for (int i = 0 ; i < _resolution.width; i += 5)
                {
                    for (int j = 0; j < _resolution.height; j += 5)
                    {
                        Vector3 direction = LocatableCameraUtils.PixelCoordToWorldCoord(camera2WorldMatrix, projectionMatrix, _resolution, new Vector2(i,j));
                        /*RaycastHit hit;
                        if (Physics.Raycast(new Vector3(0,0,0), direction, out hit, 2.0f))
                        {*/
                            Color colorPixel = _pictureTexture.GetPixel(i,j);
                            Vector3 pixelAdded = direction; //hit.point;
                            Write(path, pixelAdded, colorPixel);
                        //}
                    }
                }

            }, false);

            

        }
        sample.Dispose();
    }

    private void onVideoModeStopped(VideoCaptureResult result)
    {
        Debug.Log("Video Mode Stopped");
    }

    /// <summary>
    /// Ecrit dans un fichier les coordonnées des points du référentiel de la scène
    /// de chaque frame obtenue au fur et à mesure de l'exécution de l'application.
    /// Le fichier est au format .obj, chaque ligne est écrite sous la forme "v x y z", 
    /// "v" permettant d'indiquer que les valeurs qui suivent forment un point.
    /// </summary>
    /// <param name="path"> Le chemin du fichier </param>
    /// <param name="point"> Les coordonnées (x, y, z) d'un point </param>
    /// <param name="colorPixel"> La colour RGBA du pixel (x, y) </param>
    public static void Write(string path, Vector3 point, Color colorPixel)
    {
        File.AppendAllText(path, "v " + point.x + " " + point.y + " " + point.z + " " +
                            colorPixel[0] + " " + colorPixel[1] + " " + colorPixel[2] + Environment.NewLine);
    }
}
