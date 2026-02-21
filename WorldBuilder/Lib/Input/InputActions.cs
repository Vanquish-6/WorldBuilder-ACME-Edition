namespace WorldBuilder.Lib.Input {
    public static class InputActions {
        // Camera
        public const string CameraMoveForward = "Camera.MoveForward";
        public const string CameraMoveBackward = "Camera.MoveBackward";
        public const string CameraMoveLeft = "Camera.MoveLeft";
        public const string CameraMoveRight = "Camera.MoveRight";
        public const string CameraMoveUp = "Camera.MoveUp";
        public const string CameraMoveDown = "Camera.MoveDown";
        public const string CameraRotateLeft = "Camera.RotateLeft";
        public const string CameraRotateRight = "Camera.RotateRight";
        public const string CameraRotateUp = "Camera.RotateUp";
        public const string CameraRotateDown = "Camera.RotateDown";
        public const string CameraToggleMode = "Camera.ToggleMode";
        public const string CameraZoomIn = "Camera.ZoomIn";
        public const string CameraZoomOut = "Camera.ZoomOut";

        // Navigation
        public const string NavigationGoToLandblock = "Navigation.GoToLandblock";

        // Edit
        public const string EditUndo = "Edit.Undo";
        public const string EditRedo = "Edit.Redo";
        public const string EditRedoAlternate = "Edit.RedoAlternate";
        public const string EditCopy = "Edit.Copy";
        public const string EditPaste = "Edit.Paste";
        public const string EditDelete = "Edit.Delete";
        public const string EditCancel = "Edit.Cancel";

        // App
        public const string AppExit = "App.Exit";
    }
}
