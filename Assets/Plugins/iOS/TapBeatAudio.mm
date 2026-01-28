#import <AVFoundation/AVFoundation.h>

extern "C" {
    void TapBeat_SetAmbientAudio() {
        // Ambient 模式：与其他 App 混音，不打断音乐
        [[AVAudioSession sharedInstance] setCategory:AVAudioSessionCategoryAmbient error:nil];
        [[AVAudioSession sharedInstance] setActive:YES error:nil];
    }
}
