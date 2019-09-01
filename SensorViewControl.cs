using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Runtime.InteropServices;


#if WINDOWS_UWP
using System;
using Windows.UI;
using Windows.Media.Capture.Frames;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Devices.Core;
using Windows.Perception.Spatial;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Foundation;

using System.Runtime.InteropServices.WindowsRuntime;



[ComImport]
[Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
 {
    void GetBuffer(out byte* buffer, out uint capacity);
 }


#endif

public class SensorViewControl : MonoBehaviour {

// Données pour la projection d'un pixel en point. Données obtenues expérimentalement.
    private static float a = -0.006227f;
    private static float b = 0.000365f;
    private static float c = 0.000965f;
    private static float d = 0.006399f;

//  Taille d'une image (frame) de profondeur en format D16
    private static int width = 450;
    private static int height = 448;

// Permet d'afficher l'image via une texture 
    private Texture2D tex = null;
    private byte[] bytes = null;

// Permet d'afficher des messages à l'écran 
    public TextMesh text;

// Permet d'écrire dans un fichier sur le casque
    private string path;
    private string message;
    
// Permet la reconnaissance des gestes
    private UnityEngine.XR.WSA.Input.GestureRecognizer _gestureRecognizer;

// Bornes des valeurs de profondeur
    private static float minReliableDepth = 0.0f;
    private static float maxReliableDepth = 1.2f;

    // Autres variables utiles
    private static float depthScale = 0;
    float depth;
    private bool capture;



#if WINDOWS_UWP
    
    static Guid cameraCoordinateSystemGuid = new Guid("9D13C82F-2199-4E67-91CD-D1A4181F2534");
    /// <summary>
    /// The guid for getting the projection transform from the frame sample.
    /// See https://developer.microsoft.com/en-us/windows/mixed-reality/locatable_camera#locating_the_device_camera_in_the_world
    /// </summary>
    static Guid projectionTransformGuid = new Guid("6b761658-b7ec-4c3b-8225-8623cabec31d"); 

    /// <summary>
    /// The guid for getting the view transform from the frame sample.
    /// See https://developer.microsoft.com/en-us/windows/mixed-reality/locatable_camera#locating_the_device_camera_in_the_world
    /// </summary>
    static Guid viewTransformGuid = new Guid("4E251FA4-830F-4770-859A-4B8D99AA809B");

    // Objets nécessaires à l'obtention des images de pronfondeur
    private MediaFrameSource selectedSource;
    private MediaFrameSourceGroup selectedGroup;

    // Reférentiel d'origine (celui du monde)
    Windows.Perception.Spatial.SpatialCoordinateSystem rootSpatialCoordinateSystem;

    // Différentes matrices de transformations
    System.Numerics.Matrix4x4 cameraViewTransformMatrix;
    System.Numerics.Matrix4x4 worldTransformMatrix;
    System.Numerics.Matrix4x4 viewToWorldMatrix;
    System.Numerics.Matrix4x4 Matrix; 
#endif


    // Use this for initialization
    void Awake()
    {
        // Permet de reconnaitre un airtap et déclancher une action à ce moment
        _gestureRecognizer = new UnityEngine.XR.WSA.Input.GestureRecognizer();
#if WINDOWS_UWP
        _gestureRecognizer.TappedEvent += (source, tapCount, headRay) => { text.text = "Scan"; capture = true;};
#endif
        _gestureRecognizer.SetRecognizableGestures(UnityEngine.XR.WSA.Input.GestureSettings.Tap);
        _gestureRecognizer.StartCapturingGestures();
    }

    void Start() {
        //Text à l'écran
        text.text = "Go";
        capture = false;

        // Chemin dans l'HoloLens de création d'un fichier 3D .obj
        path = Path.Combine(Application.persistentDataPath, "screen.obj");
        File.WriteAllText(path, "");
        message = "";
#if WINDOWS_UWP
        rootSpatialCoordinateSystem = (Windows.Perception.Spatial.SpatialCoordinateSystem)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(UnityEngine.XR.WSA.WorldManager.GetNativeISpatialCoordinateSystemPtr());
        
        Task.Run(() => { InitSensor(); });
     
#endif

    }

    
    void Update () {
        
    }

#if WINDOWS_UWP
    
    private async void InitSensor()
    {   
        // Recherche les différents flux des caméras de l'HoloLens
        // Pour mieux comprendre les sources, lancer l'application SensorStreamViewer

        var mediaFrameSourceGroupList = await MediaFrameSourceGroup.FindAllAsync();
        
        // Choix du groupe de flux qui nous intéresse 
        foreach ( var mediaFrameSourceGroup in mediaFrameSourceGroupList)
        {
             selectedGroup = mediaFrameSourceGroup;

            if (mediaFrameSourceGroup.DisplayName == "Sensor Streaming")
            {
                break;
            }
        }
        
        // Paramètres qui nous intéressent
        var mediaCapture = new MediaCapture();
        var settings = new MediaCaptureInitializationSettings()
        {
            SourceGroup = selectedGroup,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        };
        try
        {   
            await mediaCapture.InitializeAsync(settings);
            
            // Choix du flux 
            foreach (var source in mediaCapture.FrameSources.Values)
            {
                // Seule la source de profondeur nous intéresse.
                if (source.Info.SourceKind == MediaFrameSourceKind.Depth)
                {
                    selectedSource = source;
                    
                }
            }

            string subtype = null;

            // Vérification de l'encodage de l'image.
            // L'encodage D16 correspond aux images de profondeur.
            foreach (MediaFrameFormat format in selectedSource.SupportedFormats)
            {
                subtype = format.Subtype;

                if (string.Equals(subtype, MediaEncodingSubtypes.D16, StringComparison.OrdinalIgnoreCase))
                {
                    await selectedSource.SetFormatAsync(format);

                    break;
                }
            }
            // Traite ensuite image par image récupérée via FrameArrived 
            var mediaframereader = await mediaCapture.CreateFrameReaderAsync(selectedSource);
            mediaframereader.FrameArrived += FrameArrived;
            
            await mediaframereader.StartAsync();
        }
        catch (Exception e)
        {
            UnityEngine.WSA.Application.InvokeOnAppThread(() => { Debug.Log(e);}, true);
        }
    }

    private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {   
        // Récupère les informations issues de l'image
        var mediaframereference = sender.TryAcquireLatestFrame();
        if (mediaframereference != null)
        {   

            var videomediaframe = mediaframereference?.VideoMediaFrame;

            // Les valeurs de profondeur via une Bitmap
            var softwarebitmap = videomediaframe?.SoftwareBitmap;            

            if (softwarebitmap != null)
            {
                if (videomediaframe.FrameReference.SourceKind == MediaFrameSourceKind.Depth)
                {
                    // Valeur du coefficient transformant les données du Bitmap en mètre 
                    depthScale = (float)videomediaframe.DepthMediaFrame.DepthFormat.DepthScaleInMeters;
                }

                if (capture)
                {   
                    // Récupération de la matrice de transformation "CameraViewTransform"
                    byte[] m = mediaframereference.Properties[viewTransformGuid] as byte[];
                    cameraViewTransformMatrix = ConvertByteArrayToMatrix4x4(m);
                        
                    // Récupération de la matrice de transformation "Transform from SpatialCoordinateSystem" méthode 1
                    SpatialCoordinateSystem cameraCoordinateSystem = mediaframereference.Properties[cameraCoordinateSystemGuid] as SpatialCoordinateSystem;
                    worldTransformMatrix = (System.Numerics.Matrix4x4)cameraCoordinateSystem.TryGetTransformTo(rootSpatialCoordinateSystem);
    
                    // Récupération de la matrice de transformation "Transform from SpatialCoordinateSystem" méthode 2 
                    // Résultat des 2 méthodes différentes, quelle est la bonne (?) 
                    var coordinateSystem = mediaframereference.CoordinateSystem;
                    System.Numerics.Matrix4x4 worldTransformMatrix2 = (System.Numerics.Matrix4x4)coordinateSystem.TryGetTransformTo(rootSpatialCoordinateSystem);

                    // Plusieurs transformation des matrices (Transposition, invertion, multiplication)
                    // Vue dans les différents forums cités dans le wiki
                    worldTransformMatrix = System.Numerics.Matrix4x4.Transpose(worldTransformMatrix);
                    cameraViewTransformMatrix = System.Numerics.Matrix4x4.Transpose(cameraViewTransformMatrix);

                    System.Numerics.Matrix4x4 mat = cameraViewTransformMatrix;
                    System.Numerics.Matrix4x4.Invert(mat, out cameraViewTransformMatrix);

                    viewToWorldMatrix = System.Numerics.Matrix4x4.Multiply(worldTransformMatrix, cameraViewTransformMatrix);

                    //cameraViewTransformMatrix = System.Numerics.Matrix4x4.Transpose(worldTransformMatrix2);

                    // Unity travaille dans un repère indirect et Microsoft dans un repère direct
                    // Il faut donc réaliser une convertion de la matrice obtenue
                    worldTransformMatrix2.M31 *= -1f;
                    worldTransformMatrix2.M32 *= -1f;
                    worldTransformMatrix2.M33 *= -1f;
                    worldTransformMatrix2.M34 *= -1f;

                    Matrix = worldTransformMatrix2;

                    // Eventuellement utile pour convertir une matrice 4x4 de Unity en matrice 4x4 du System.Numerics
                    //float[] viewToWorldTableau = ConvertMatrixToFloatArray(viewToWorldMatrix);
                    //Matrix = ConvertFloatArrayToMatrix4x4(viewToWorldTableau);
                    
                }

                // Traitement de l'image Bitmap pour afficher une image visible/cohérente
                softwarebitmap = EditBitmapPixels(softwarebitmap);
                //softwarebitmap = SoftwareBitmap.Convert(softwarebitmap, BitmapPixelFormat.Rgba8, BitmapAlphaMode.Premultiplied);

                int w = softwarebitmap.PixelWidth;
                int h = softwarebitmap.PixelHeight;
                if (bytes == null)
                {
                    bytes = new byte[w * h * 4];
                }
                softwarebitmap.CopyToBuffer(bytes.AsBuffer());
                softwarebitmap.Dispose();
                UnityEngine.WSA.Application.InvokeOnAppThread(() => {
                    
                    if (tex == null)
                    {
                        tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                        GetComponent<Renderer>().material.mainTexture = tex;
                    }
                    tex.LoadRawTextureData(bytes);
                    tex.Apply();                  
                    

                    if (capture)
                    {
                        File.AppendAllText(path, message);    
                        capture = false; 
                        text.text  = "Done";
                    }
                }, true);
            }
            mediaframereference.Dispose();
        }
    }

    /// <summary>
    /// Lecture des données contenues dans l'image bitmap.
    /// </summary>
    /// <param name="input"> L'image bitmap à traiter </param>
    private unsafe SoftwareBitmap EditBitmapPixels(SoftwareBitmap input)
    {
        int pixelWidth = input.PixelWidth;
        int pixelHeight = input.PixelHeight;

        // Création de l'image de sortie.
        SoftwareBitmap output = new SoftwareBitmap(BitmapPixelFormat.Bgra8,
                                                    pixelWidth,
                                                    pixelHeight,
                                                    BitmapAlphaMode.Premultiplied);

        // Création des buffers de lecture et d'écriture.
        using (BitmapBuffer inputBuffer = input.LockBuffer(BitmapBufferAccessMode.Read),
                            outputBuffer = output.LockBuffer(BitmapBufferAccessMode.Write))
        {
            BitmapPlaneDescription inputBufferLayout = inputBuffer.GetPlaneDescription(0);
            BitmapPlaneDescription outputBufferLayout = outputBuffer.GetPlaneDescription(0);

            // Création de références tampons sur les buffers.
            using (IMemoryBufferReference inputReference = inputBuffer.CreateReference(),
                                          outputReference = outputBuffer.CreateReference())
            {
                // Récupération des octets des buffers d'entrée.
                byte* inputBytes;
                uint inputCapacity;
                ((IMemoryBufferByteAccess)inputReference).GetBuffer(out inputBytes, out inputCapacity);

                // Récupération des octets des buffers de sortie.
                byte* outputBytes;
                uint outputCapacity;
                ((IMemoryBufferByteAccess)outputReference).GetBuffer(out outputBytes, out outputCapacity);

                // Coloration des pixels selon la valeur de la profondeur.


                for (int y = 0; y < pixelHeight; y++)
                {
                    byte* inputRowBytes = inputBytes + y * inputBufferLayout.Stride;
                    byte* outputRowBytes = outputBytes + y * outputBufferLayout.Stride;

                    for (int x = 0; x < pixelWidth; x++)
                    {
                        if (capture && (x+y)%2 == 0)
                        {
                            message += SavePoint(x, y, inputRowBytes,
                                                   depthScale, minReliableDepth, maxReliableDepth, Matrix);
                        }
                        ColorUtil.ColorForDepth(x, y, inputRowBytes, outputRowBytes,
                                                   depthScale, minReliableDepth, maxReliableDepth);
                    }
                }

            }

            return output;
        }

    }

    private System.Numerics.Matrix4x4 ConvertByteArrayToMatrix4x4(byte[] matrixAsBytes)
        {
            var m = matrixAsBytes;
            return new System.Numerics.Matrix4x4(
                System.BitConverter.ToSingle(m, 0),
                System.BitConverter.ToSingle(m, 4),
                BitConverter.ToSingle(m, 8),
                BitConverter.ToSingle(m, 12),
                BitConverter.ToSingle(m, 16),
                BitConverter.ToSingle(m, 20),
                BitConverter.ToSingle(m, 24),
                BitConverter.ToSingle(m, 28),
                BitConverter.ToSingle(m, 32),
                BitConverter.ToSingle(m, 36),
                BitConverter.ToSingle(m, 40),
                BitConverter.ToSingle(m, 44),
                BitConverter.ToSingle(m, 48),
                BitConverter.ToSingle(m, 52),
                BitConverter.ToSingle(m, 56),
                BitConverter.ToSingle(m, 60));
        }
    
    /// <summary>
    /// Helper method for converting into UnityEngine.Matrix4x4
    /// </summary>
    /// <param name="matrixAsArray"></param>
    /// <returns></returns>
    public static UnityEngine.Matrix4x4 ConvertFloatArrayToMatrix4x4(float[] matrixAsArray)
    {
        //There is probably a better way to be doing this but System.Numerics.Matrix4x4 is not available 
        //in Unity and we do not include UnityEngine in the plugin.
        UnityEngine.Matrix4x4 m = new UnityEngine.Matrix4x4();
        m.m00 = matrixAsArray[0];
        m.m01 = matrixAsArray[1];
        m.m02 = -matrixAsArray[2];
        m.m03 = matrixAsArray[3];
        m.m10 = matrixAsArray[4];
        m.m11 = matrixAsArray[5];
        m.m12 = -matrixAsArray[6];
        m.m13 = matrixAsArray[7];
        m.m20 = matrixAsArray[8];
        m.m21 = matrixAsArray[9];
        m.m22 = -matrixAsArray[10];
        m.m23 = matrixAsArray[11];
        m.m30 = matrixAsArray[12];
        m.m31 = matrixAsArray[13];
        m.m32 = matrixAsArray[14];
        m.m33 = matrixAsArray[15];

        return m;
    }

    private float[] ConvertMatrixToFloatArray(System.Numerics.Matrix4x4 matrix)
    {
        return new float[16] {
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
    }

    public static unsafe string SavePoint(int x, int y, byte* inputRowBytes, 
                                                float depthScale, float minReliableDepth, float maxReliableDepth, 
                                                System.Numerics.Matrix4x4 Matrix)
    {   
            
        ushort* inputRow = (ushort*)inputRowBytes;
        float depth = inputRow[x] * depthScale;

        if (depth < 2)
        {   string mes = "";
            Vector2 realPoint = PixelToWorldCoordonate(x, y);
            
            System.Numerics.Vector3 norme = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(realPoint.x, realPoint.y, 1.0f));
            norme *= depth;
            System.Numerics.Vector3 position = System.Numerics.Vector3.Transform(norme, Matrix);

            /*float norm = Mathf.Sqrt(realPoint.x * realPoint.x + realPoint.y * realPoint.y + 1);
            depth = depth / norm;
            realPoint *= depth;
            Vector3 dirRay = new Vector3(realPoint.x, realPoint.y, - depth);
            Vector3 direction = new Vector3(Vector3.Dot(Matrix.GetRow(0), dirRay), Vector3.Dot(Matrix.GetRow(1), dirRay), Vector3.Dot(Matrix.GetRow(2), dirRay));
            Vector3 position = new Vector3(Matrix.GetColumn(3).x, Matrix.GetColumn(3).y, Matrix.GetColumn(3).z) + direction;
            
            mes += "v " + position.x.ToString() + " " + position.y.ToString() + " " + position.z.ToString() + " 0 0 1" + Environment.NewLine;        

            Vector4 positio = new Vector4(realPoint.x, realPoint.y, depth,1.0f);
            positio = Matrix.MultiplyVector(positio);*/

            
            return "v " + position.X.ToString() + " " + position.Y.ToString() + " " + position.Z.ToString() + " 1 0 0" + Environment.NewLine;
        }

        return "";
    }


    private static Vector2 PixelToWorldCoordonate(int u, int v)
    // Fonction extraite de l'analyse de CameraImagePointToUnitPlane
    {
        float x = a * (u - width / 2) + b * (v - height / 2);
        float y = c * (u - width / 2) + d * (v - height / 2);
        return new Vector2(x, y);
    }
#endif
}
