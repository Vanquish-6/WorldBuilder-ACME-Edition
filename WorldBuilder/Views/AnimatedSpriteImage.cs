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

        public AnimatedSpriteImage() {
            AffectsRender<AnimatedSpriteImage>(SourceProperty);
            AffectsRender<AnimatedSpriteImage>(FrameCountProperty);
            AffectsRender<AnimatedSpriteImage>(FrameWidthProperty);
        }

        protected override void OnPointerEntered(PointerEventArgs e) {
            base.OnPointerEntered(e);
            _isHovering = true;
            UpdateAnimationState();
        }

        protected override void OnPointerExited(PointerEventArgs e) {
            base.OnPointerExited(e);
            _isHovering = false;
            UpdateAnimationState();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
            base.OnPropertyChanged(change);
            if (change.Property == SourceProperty) {
                // Keep the current frame if possible, or reset if it's a completely new load context
                // But for "progressive upgrading" (static -> animated), we might want to start animating immediately
                // if we are currently hovering.
                // _currentFrame = 0; // Don't strictly reset, allowing seamless transition if frame 0 matches

                InvalidateVisual();
                UpdateAnimationState(); // Re-evaluate animation eligibility (e.g. now we have more frames)
            }
            else if (change.Property == AnimateOnHoverProperty) {
                UpdateAnimationState();
            }
        }

        private void UpdateAnimationState() {
            bool shouldAnimate = AnimateOnHover && _isHovering && Source != null && FrameCount > 1;

            if (shouldAnimate) {
                if (_animationTimer == null) {
                    _animationTimer = new DispatcherTimer {
                        Interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond)
                    };
                    _animationTimer.Tick += (s, e) => {
                        _currentFrame = (_currentFrame + 1) % FrameCount;
                        InvalidateVisual();
                    };
                }
                if (!_animationTimer.IsEnabled) {
                    _animationTimer.Start();
                }
            }
            else {
                if (_animationTimer != null && _animationTimer.IsEnabled) {
                    _animationTimer.Stop();
                }
                _currentFrame = 0; // Reset to first frame when not animating
                InvalidateVisual();
            }
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
