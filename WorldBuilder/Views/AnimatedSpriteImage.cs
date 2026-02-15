using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;

namespace WorldBuilder.Views {
    public class AnimatedSpriteImage : Control {
        public static readonly StyledProperty<Bitmap?> SourceProperty =
            AvaloniaProperty.Register<AnimatedSpriteImage, Bitmap?>(nameof(Source));

        public Bitmap? Source {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public static readonly StyledProperty<int> FrameCountProperty =
            AvaloniaProperty.Register<AnimatedSpriteImage, int>(nameof(FrameCount), defaultValue: 1);

        public int FrameCount {
            get => GetValue(FrameCountProperty);
            set => SetValue(FrameCountProperty, value);
        }

        public static readonly StyledProperty<int> FrameWidthProperty =
            AvaloniaProperty.Register<AnimatedSpriteImage, int>(nameof(FrameWidth), defaultValue: 96);

        public int FrameWidth {
            get => GetValue(FrameWidthProperty);
            set => SetValue(FrameWidthProperty, value);
        }

        public static readonly StyledProperty<bool> AnimateOnHoverProperty =
            AvaloniaProperty.Register<AnimatedSpriteImage, bool>(nameof(AnimateOnHover), defaultValue: true);

        public bool AnimateOnHover {
            get => GetValue(AnimateOnHoverProperty);
            set => SetValue(AnimateOnHoverProperty, value);
        }

        public static readonly StyledProperty<double> FramesPerSecondProperty =
            AvaloniaProperty.Register<AnimatedSpriteImage, double>(nameof(FramesPerSecond), defaultValue: 24.0);

        public double FramesPerSecond {
            get => GetValue(FramesPerSecondProperty);
            set => SetValue(FramesPerSecondProperty, value);
        }

        private int _currentFrame = 0;
        private DispatcherTimer? _animationTimer;
        private bool _isHovering;
        private double _currentFps;
        private bool _isSlowingDown;

        public AnimatedSpriteImage() {
            AffectsRender<AnimatedSpriteImage>(SourceProperty);
            AffectsRender<AnimatedSpriteImage>(FrameCountProperty);
            AffectsRender<AnimatedSpriteImage>(FrameWidthProperty);
        }

        protected override void OnPointerEntered(PointerEventArgs e) {
            base.OnPointerEntered(e);
            _isHovering = true;
            _isSlowingDown = false;
            _currentFps = FramesPerSecond;
            UpdateAnimationState();
        }

        protected override void OnPointerExited(PointerEventArgs e) {
            base.OnPointerExited(e);
            _isHovering = false;
            _isSlowingDown = true;
            UpdateAnimationState();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
            if (_animationTimer != null) {
                _animationTimer.Stop();
                _animationTimer.Tick -= OnAnimationTick;
                _animationTimer = null;
            }
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == SourceProperty) {
                // Keep the current frame if possible, allowing seamless transition if frame 0 matches
                InvalidateVisual();
                UpdateAnimationState(); // Re-evaluate animation eligibility (e.g. now we have more frames)
            }
            else if (change.Property == AnimateOnHoverProperty) {
                UpdateAnimationState();
            }
        }

        private void UpdateAnimationState() {
            bool canAnimate = Source != null && FrameCount > 1;
            bool shouldAnimate = canAnimate && (_isHovering || _isSlowingDown);

            if (shouldAnimate) {
                if (_animationTimer == null) {
                    _animationTimer = new DispatcherTimer();
                    _animationTimer.Tick += OnAnimationTick;
                }

                if (!_animationTimer.IsEnabled) {
                    _currentFps = FramesPerSecond;
                    _animationTimer.Interval = TimeSpan.FromSeconds(1.0 / _currentFps);
                    _animationTimer.Start();
                }
            }
            else {
                if (_animationTimer != null && _animationTimer.IsEnabled) {
                    _animationTimer.Stop();
                }
                _currentFrame = 0; // Reset to first frame when fully stopped
                InvalidateVisual();
            }
        }

        private void OnAnimationTick(object? sender, EventArgs e) {
            if (_isSlowingDown) {
                // Decay FPS
                _currentFps *= 0.90; // Reduce speed by 10% each frame
                if (_currentFps < 2.0) { // Stop if too slow
                    _isSlowingDown = false;
                    UpdateAnimationState(); // This will stop the timer
                    return;
                }
                if (_animationTimer != null) {
                    _animationTimer.Interval = TimeSpan.FromSeconds(1.0 / _currentFps);
                }
            }

            _currentFrame = (_currentFrame + 1) % FrameCount;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context) {
            var source = Source;
            if (source == null) return;

            // Calculate source rect
            // Frames are arranged horizontally
            int frameW = FrameWidth;
            int frameCount = FrameCount;

            // Safety check: if the source image is too small for the requested frames,
            // fallback to displaying the whole image as a single frame.
            // This handles legacy cached thumbnails (96x96) when UI expects sprite sheet (768x96).
            if (source.Size.Width < frameW * frameCount) {
                frameW = (int)source.Size.Width;
                frameCount = 1;
            }

            if (frameCount <= 1) {
                frameW = (int)source.Size.Width;
            }

            // Ensure frame index is valid
            int drawFrame = _currentFrame;
            if (drawFrame >= frameCount) drawFrame = 0;

            var srcRect = new Rect(drawFrame * frameW, 0, frameW, source.Size.Height);
            var destRect = new Rect(Bounds.Size);

            // Center and scale to fit (Uniform)
            // But usually this control is inside a container of fixed size.
            // We'll mimic Image Stretch="Uniform" logic simplified:
            // Calculate scale to fit destRect
            double scale = Math.Min(destRect.Width / srcRect.Width, destRect.Height / srcRect.Height);
            double w = srcRect.Width * scale;
            double h = srcRect.Height * scale;
            double x = (destRect.Width - w) / 2;
            double y = (destRect.Height - h) / 2;

            context.DrawImage(source, srcRect, new Rect(x, y, w, h));
        }
    }
}
