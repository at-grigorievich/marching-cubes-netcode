using Mine_Generator.Data;
using MineGenerator.Interfaces;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

#if UNITY_EDITOR
namespace MineGenerator
{
    [EditorTool("Chunks Weight Editor Tool")]
    public class ChunksWeightEditorTool: EditorTool
    {
        public const string RadiusRef = "minechunk-radius";
        public const string IntensityRef = "minechunk-intensity";
        public const string SelectColorRef = "minechunk-select-color";
        public const string UnselectColorRef = "minechunk-unselect-color";
        
        private static Texture2D _toolIcon;
        
        private readonly GUIContent _iconContent = new GUIContent
        {
            image = _toolIcon,
            text = "Chunks Weight Editor Tool",
            tooltip = "Chunks Weight Editor Tool"
        };

        private EnumField _modifyType;
        private FloatField _radius;
        private FloatField _intensity;
        private ColorField _selectColor;
        private ColorField _unselectColor;

        private VisualElement _toolRootElement;

        private bool _allowModify;
        
        public override GUIContent toolbarIcon
        {
            get { return _iconContent; }
        }

        public override void OnActivated()
        {
            _toolRootElement = new VisualElement();
            _toolRootElement.style.width = 250f;
            
            var backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.21f, 0.21f, 0.21f, 0.8f)
                : new Color(0.8f, 0.8f, 0.8f, 0.8f);
            _toolRootElement.style.backgroundColor = backgroundColor;
            _toolRootElement.style.marginLeft = 10f;
            _toolRootElement.style.marginBottom = 10f;
            _toolRootElement.style.paddingTop = 5f;
            _toolRootElement.style.paddingRight = 5f;
            _toolRootElement.style.paddingLeft = 5f;
            _toolRootElement.style.paddingBottom = 5f;
            var titleLabel = new Label("Place Objects Tool");
            titleLabel.style.unityTextAlign = TextAnchor.MiddleLeft;

            _modifyType = new EnumField("Тип модификации", ModifyType.Decrease);
            _radius = new FloatField("Радиус воздействия");
            _intensity = new FloatField("Интенсивность воздействия");
            _selectColor = new ColorField("Выбран");
            _unselectColor = new ColorField("Не выбран");

            _radius.value = LoadRadius();
            _intensity.value = LoadIntensity();
            _selectColor.value = LoadSelectedColor();
            _unselectColor.value = LoadUnselectedColor();

            _toolRootElement.Add(_modifyType);
            _toolRootElement.Add(_radius);
            _toolRootElement.Add(_intensity);
            _toolRootElement.Add(_selectColor);
            _toolRootElement.Add(_unselectColor);

            var sv = SceneView.lastActiveSceneView;
            
            sv.rootVisualElement.Add(_toolRootElement);
            sv.rootVisualElement.style.flexDirection = FlexDirection.ColumnReverse;
        }
        
        public override void OnWillBeDeactivated()
        {
            SaveFloat(RadiusRef,_radius.value);
            SaveFloat(IntensityRef,_intensity.value);
            SaveColor(SelectColorRef,_selectColor.value);
            SaveColor(UnselectColorRef,_unselectColor.value);

            _toolRootElement?.RemoveFromHierarchy();
        }
        
        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView)) return;
            if (!ToolManager.IsActiveTool(this)) return;
            
            int id = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(id);
            
            GetAllowModify();
            CastWeightEditors();
            window.Repaint();
        }
        
        private void CastWeightEditors()
        {
            if(Event.current.type != EventType.Repaint 
               && Event.current.type != EventType.MouseDown
               && Event.current.type != EventType.MouseDrag) return;
            
            RaycastHit _hit;
            
            var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);

            if (Physics.Raycast(ray, out _hit))
            {
                if (_hit.transform.TryGetComponent(out IWeightEditable weightEditor))
                {
                    Handles.color = _allowModify ? _selectColor.value : _unselectColor.value;
                    Handles.SphereHandleCap(0,_hit.point, Quaternion.identity, _radius.value, EventType.Repaint);
                    
                    if (_allowModify) 
                        weightEditor.UpdateWeight(new WeightModifyData(_hit.point, _radius.value, _intensity.value, (ModifyType)_modifyType.value));
                }
            }
        }
        
        private void GetAllowModify()
        {
            if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseDown)
            {
                if (Event.current.button == 0)
                {
                    _allowModify = true;
                }
            }

            if (Event.current.type == EventType.MouseUp)
            {
                _allowModify = false;
            }
        }
        
        private float LoadRadius()
        {
            if (EditorPrefs.HasKey(RadiusRef))
            {
                return EditorPrefs.GetFloat(RadiusRef);
            }
            return 3f;
        }
        private float LoadIntensity()
        {
            if (EditorPrefs.HasKey(IntensityRef))
            {
                return EditorPrefs.GetFloat(IntensityRef);
            }
            return 0.01f;
        }

        private Color LoadSelectedColor()
        {
            if (EditorPrefs.HasKey(SelectColorRef))
            {
                ColorUtility.TryParseHtmlString(EditorPrefs.GetString(SelectColorRef), out Color color);
                return color;
            }
            return Color.red;
        }
        private Color LoadUnselectedColor()
        {
            if (EditorPrefs.HasKey(UnselectColorRef))
            {
                ColorUtility.TryParseHtmlString(EditorPrefs.GetString(UnselectColorRef), out Color color);
                return color;
            }
            return Color.yellow;
        }
        
        private void SaveFloat(string reference, float value)
        {
            EditorPrefs.SetFloat(reference,value);
        }
        private void SaveColor(string reference, Color color)
        {
            string res = $"#{ColorUtility.ToHtmlStringRGBA(color)}";
            EditorPrefs.SetString(reference, res);
        }
    }
}
#endif