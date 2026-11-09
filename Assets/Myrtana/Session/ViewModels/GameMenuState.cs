using System;
using Myrtana.Core;

namespace Myrtana.Session
{
    /// <summary>The pages of the in-game menu (<c>gCHUDDialogMenu2</c>; Docs/formats-gui.md §6.12).</summary>
    public enum MenuPage { Closed, Main, Save, Load, Options, Audio, Controls, Video, Difficulty, Performance, PerformanceCustom, ControlsCustom, Remaster,
        /// <summary>gamepad-1: the Gamepad options (from the Controls page) and its pad key list.</summary>
        Gamepad, GamepadCustom,
        /// <summary>opts-1: the Remaster page's "World details" (desert sand, dunes, desert atmosphere, storm sand, saturation).</summary>
        RemasterDetails }

    /// <summary>A pending confirm box (<c>IDD_MENU_CONFIRM_2</c>): a message with Yes/No, or a notice with OK only.</summary>
    public sealed class MenuRequest
    {
        public MenuRequest(TextId message, Action confirmed = null, bool notice = false)
        {
            Message = message;
            Confirmed = confirmed;
            IsNotice = notice;
        }

        /// <summary>
        /// A question in the port's own words (G3's string tables have none, fix-155's crash recovery):
        /// <paramref name="text"/> gives it in the current language; <paramref name="declined"/> runs on No / Esc.
        /// </summary>
        public MenuRequest(Func<string> text, Action confirmed, Action declined)
        {
            MessageText = text;
            Confirmed = confirmed;
            Declined = declined;
        }

        public TextId Message { get; }
        /// <summary>The port's own message text (overrides <see cref="Message"/>), or null.</summary>
        public Func<string> MessageText { get; }
        /// <summary>Runs on Yes (or OK); never on No/Esc.</summary>
        public Action Confirmed { get; }
        /// <summary>Runs on No (or Esc); never on Yes.</summary>
        public Action Declined { get; }
        /// <summary>Only an OK button (a refusal such as "Save not possible at present!").</summary>
        public bool IsNotice { get; }
    }

    /// <summary>
    /// Navigation of the in-game menu: Esc opens the main page, Back/Esc returns from a sub-page (performance, video,
    /// the port's remaster page, audio, controls and difficulty to the options, the custom key list to controls, the custom details to performance, the World details to the remaster page,
    /// the rest to the main page) and closes the menu from the main page; an open confirm box takes Esc
    /// first and answers No. The host pauses the game while <see cref="IsOpen"/>.
    /// </summary>
    public sealed class GameMenuState
    {
        public MenuPage Page { get; private set; } = MenuPage.Closed;
        public MenuRequest Request { get; private set; }
        public bool IsOpen => Page != MenuPage.Closed;
        /// <summary>The title screen: the main page has no game to return to, so Back stays on it (Esc asks to quit there).</summary>
        public bool AtTitle { get; set; }

        /// <summary>Raised after every change of page or request.</summary>
        public event Action Changed;

        public void Open() => Show(MenuPage.Main);

        public void Close()
        {
            Request = null;
            Show(MenuPage.Closed);
        }

        public void Show(MenuPage page)
        {
            if (page == Page) return;
            Page = page;
            Changed?.Invoke();
        }

        /// <summary>One step back: closes a confirm box, else returns to the parent page; true while the menu stays open.</summary>
        public bool Back()
        {
            if (Request != null)
            {
                Answer(false);
                return IsOpen;
            }
            var parent = Parent(Page);
            if (parent == MenuPage.Closed && AtTitle) return true;
            Show(parent);
            return IsOpen;
        }

        public static MenuPage Parent(MenuPage page)
        {
            switch (page)
            {
                case MenuPage.Audio:
                case MenuPage.Controls:
                case MenuPage.Video:
                case MenuPage.Performance:
                case MenuPage.Remaster:
                case MenuPage.Difficulty: return MenuPage.Options;
                case MenuPage.PerformanceCustom: return MenuPage.Performance;
                case MenuPage.ControlsCustom:
                case MenuPage.Gamepad: return MenuPage.Controls;
                case MenuPage.GamepadCustom: return MenuPage.Gamepad;
                case MenuPage.RemasterDetails: return MenuPage.Remaster;
                case MenuPage.Save:
                case MenuPage.Load:
                case MenuPage.Options: return MenuPage.Main;
                default: return MenuPage.Closed;
            }
        }

        public void Ask(MenuRequest request)
        {
            Request = request;
            Changed?.Invoke();
        }

        /// <summary>Closes the confirm box; Yes/OK runs its action afterwards (the action may change the page).</summary>
        public void Answer(bool yes)
        {
            var request = Request;
            if (request == null) return;
            Request = null;
            Changed?.Invoke();
            if (yes) request.Confirmed?.Invoke();
            else request.Declined?.Invoke();
        }
    }
}
