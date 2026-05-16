// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.IO;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Animations;
using osu.Framework.Graphics.Textures;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Game.Skinning;
using osuTK;

namespace osu.Game.Storyboards.Drawables
{
    public partial class DrawableStoryboardAnimation : TextureAnimation, IFlippable, IVectorScalable
    {
        public StoryboardAnimation Animation { get; }

        public bool FlipH
        {
            get;
            set
            {
                if (field == value)
                    return;

                field = value;
                Invalidate(Invalidation.MiscGeometry);
            }
        }

        public bool FlipV
        {
            get;
            set
            {
                if (field == value)
                    return;

                field = value;
                Invalidate(Invalidation.MiscGeometry);
            }
        }

        private Vector2 vectorScale = Vector2.One;

        public Vector2 VectorScale
        {
            get => vectorScale;
            set
            {
                if (vectorScale == value)
                    return;

                if (!Validation.IsFinite(value)) throw new ArgumentException($@"{nameof(VectorScale)} must be finite, but is {value}.");

                vectorScale = value;
                Invalidate(Invalidation.MiscGeometry);
            }
        }

        public override bool RemoveWhenNotAlive => false;

        protected override System.Numerics.Vector2 DrawScale
            => new System.Numerics.Vector2(
                (FlipH ? -base.DrawScale.X : base.DrawScale.X) * VectorScale.X,
                (FlipV ? -base.DrawScale.Y : base.DrawScale.Y) * VectorScale.Y);

        public override Anchor Origin => StoryboardExtensions.AdjustOrigin(base.Origin, VectorScale, FlipH, FlipV);

        public override bool IsPresent
            => !float.IsNaN(DrawPosition.X) && !float.IsNaN(DrawPosition.Y) && base.IsPresent;

        public DrawableStoryboardAnimation(StoryboardAnimation animation)
        {
            Animation = animation;
            Origin = animation.Origin;
            Position = animation.InitialPosition;
            Loop = animation.LoopType == AnimationLoopType.LoopForever;
            Name = animation.Path;

            LifetimeStart = animation.StartTime;
            LifetimeEnd = animation.EndTimeForDisplay;
        }

        protected override void Update()
        {
            base.Update();

            // In stable, alpha transforms exceeding values of 1 would result in sprites disappearing from view.
            // See https://github.com/peppy/osu-stable-reference/blob/08e3dafd525934cf48880b08e91c24ce4ad8b761/osu!/Graphics/Sprites/pSprite.cs#L413-L414
            //
            // Over the years, storyboard(ers) have taken advantage of this to create "flicker" patterns.
            // This is quite a common technique, so we are reproducing it here for now.
            //
            // NOTE TO FUTURE VISITORS: If we do ever update the storyboard spec, we may want to move such flicker effects to their
            // own transform type, and make this a legacy behaviour. It feels very flimsy.
            if (Alpha > 1) Alpha %= 1;
        }

        [Resolved]
        private ISkinSource skin { get; set; }

        [Resolved]
        private IBeatSyncProvider beatSyncProvider { get; set; }

        [Resolved]
        private TextureStore textureStore { get; set; }

        [BackgroundDependencyLoader]
        private void load(Storyboard storyboard)
        {
            if (storyboard.UseSkinSprites)
            {
                skin.SourceChanged += skinSourceChanged;
                skinSourceChanged();
            }
            else
                addFramesFromStoryboardSource();

            Animation.ApplyTransforms(this);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Framework animation class tries its best to synchronise the animation at LoadComplete,
            // but in some cases (such as fast forward) this results in an incorrect start offset.
            //
            // In the case of storyboard animations, we want to synchronise with game time perfectly
            // so let's get a correct time based on gameplay clock and earliest transform.
            PlaybackPosition = beatSyncProvider.Clock.CurrentTime - Animation.EarliestTransformTime;
        }

        private void skinSourceChanged()
        {
            ClearFrames();

            // Prefer the storyboard's LargeTextureStore (backed by the beatmap folder) so that large
            // storyboard animation frames are never routed through the skin's regular atlased TextureStore.
            // Only fall back to skin lookup when the first animation frame is absent from the beatmap
            // folder (i.e. it is a standard skin element like "hit300"), matching stable's "UseSkinSprites"
            // semantics.
            string firstFramePath = Animation.FrameCount > 0
                ? Animation.Path.Replace(".", "0.")
                : Animation.Path;

            if (textureStore.Get(firstFramePath) != null)
            {
                addFramesFromStoryboardSource();
                return;
            }

            // Fall back to skin (original UseSkinSprites behaviour): when reading from a skin, we match
            // stable's weird behaviour where `FrameCount` is ignored and resources are retrieved until
            // the end of the animation.
            var skinTextures = skin.GetTextures(Path.ChangeExtension(Animation.Path, null), default, default, true, string.Empty, null, out _);

            if (skinTextures.Length > 0)
            {
                foreach (var texture in skinTextures)
                    AddFrame(texture, Animation.FrameDelay);
            }
            else
            {
                addFramesFromStoryboardSource();
            }
        }

        private void addFramesFromStoryboardSource()
        {
            int frameIndex;
            // sourcing from storyboard.
            for (frameIndex = 0; frameIndex < Animation.FrameCount; frameIndex++)
                AddFrame(textureStore.Get(getFramePath(frameIndex)), Animation.FrameDelay);

            string getFramePath(int i) => Animation.Path.Replace(".", $"{i}.");
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            skin?.SourceChanged -= skinSourceChanged;
        }
    }
}
