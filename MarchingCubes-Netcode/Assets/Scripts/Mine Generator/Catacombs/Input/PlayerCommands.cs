using UnityEngine;

namespace MineGenerator.Catacombs
{
    /// <summary>
    /// Что игрок велел сделать в этом кадре — независимо от того, чем: мышью и клавиатурой
    /// или пальцами (M5). Стенд и оружие читают команды, а не <c>Input</c> напрямую:
    /// иначе тач пришлось бы вписывать в каждого, кто слушает кнопку.
    ///
    /// «Held» — держится, «Pressed» — нажато именно в этом кадре.
    /// </summary>
    public struct PlayerCommands
    {
        /// <summary>Ходьба: x — вправо, y — вперёд, каждая ось от −1 до 1.</summary>
        public Vector2 Move;

        /// <summary>
        /// Поворот взгляда в единицах оси мыши — как <c>Input.GetAxisRaw("Mouse X")</c>.
        /// В градусы переводит чувствительность стенда; тач приводит свои пиксели к этим же
        /// единицам, и чувствительность у игрока остаётся одна.
        /// </summary>
        public Vector2 Look;

        public bool FireHeld;
        public bool FirePressed;

        /// <summary>Альтернативный огонь (ПКМ). Пока им заращивается порода при включённом копании.</summary>
        public bool AltHeld;
        public bool AltPressed;

        public bool Reload;

        /// <summary>Прыжок; в полёте — вверх.</summary>
        public bool JumpHeld;
        public bool JumpPressed;

        /// <summary>В полёте — вниз.</summary>
        public bool Descend;

        /// <summary>Ускорение. В M1 Shift уходит под рывок.</summary>
        public bool Sprint;

        /// <summary>Действие у предмета: разбудить Матку у логова.</summary>
        public bool Interact;

        /// <summary>Отпустить курсор (Esc); в M1 — меню паузы.</summary>
        public bool Cancel;

        /// <summary>Колесо: пока — радиус кисти копания, в M2 — смена пушки.</summary>
        public float Scroll;
    }

    /// <summary>Источник команд: клавиатура с мышью, тач, бот прогона.</summary>
    public interface IPlayerInputSource
    {
        /// <summary>Заполняет команды этого кадра. Поля, которых источник не знает, не трогает.</summary>
        void Read(ref PlayerCommands commands);
    }

    /// <summary>
    /// Клавиатура и мышь через старый Input Manager (<c>activeInputHandler: 0</c>). Раскладка
    /// та же, что была зашита в стенд и гранатомёт до появления команд.
    /// </summary>
    public sealed class DesktopInputSource : IPlayerInputSource
    {
        public void Read(ref PlayerCommands commands)
        {
            commands.Move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            commands.Look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));

            commands.FireHeld = Input.GetMouseButton(0);
            commands.FirePressed = Input.GetMouseButtonDown(0);
            commands.AltHeld = Input.GetMouseButton(1);
            commands.AltPressed = Input.GetMouseButtonDown(1);

            commands.Reload = Input.GetKeyDown(KeyCode.V);

            commands.JumpHeld = Input.GetKey(KeyCode.Space);
            commands.JumpPressed = Input.GetKeyDown(KeyCode.Space);
            commands.Descend = Input.GetKey(KeyCode.LeftControl);
            commands.Sprint = Input.GetKey(KeyCode.LeftShift);

            commands.Interact = Input.GetKeyDown(KeyCode.E);
            commands.Cancel = Input.GetKeyDown(KeyCode.Escape);
            commands.Scroll = Input.mouseScrollDelta.y;
        }
    }
}
