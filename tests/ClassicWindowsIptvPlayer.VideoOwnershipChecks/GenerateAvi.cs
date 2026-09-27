using System;
using System.IO;
using System.Text;

public static class GenerateAvi
{
    public static void Run(string path)
    {
        const int width = 160, height = 90, fps = 10, frames = 300;
        const int stride = width * 3, imageSize = stride * height;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        void FourCC(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        void Patch(long at, long value) { var end = stream.Position; stream.Position = at; writer.Write(checked((int)value)); stream.Position = end; }
        FourCC("RIFF"); var riffSize = stream.Position; writer.Write(0); FourCC("AVI ");
        FourCC("LIST"); var hdrlSize = stream.Position; writer.Write(0); FourCC("hdrl");
        FourCC("avih"); writer.Write(56);
        writer.Write(1000000 / fps); writer.Write(imageSize * fps); writer.Write(0); writer.Write(0x10);
        writer.Write(frames); writer.Write(0); writer.Write(1); writer.Write(imageSize);
        writer.Write(width); writer.Write(height); for (var i = 0; i < 4; i++) writer.Write(0);
        FourCC("LIST"); var strlSize = stream.Position; writer.Write(0); FourCC("strl");
        FourCC("strh"); writer.Write(56); FourCC("vids"); FourCC("DIB ");
        writer.Write(0); writer.Write((short)0); writer.Write((short)0); writer.Write(0);
        writer.Write(1); writer.Write(fps); writer.Write(0); writer.Write(frames);
        writer.Write(imageSize); writer.Write(-1); writer.Write(0);
        writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height);
        FourCC("strf"); writer.Write(40); writer.Write(40); writer.Write(width); writer.Write(height);
        writer.Write((short)1); writer.Write((short)24); writer.Write(0); writer.Write(imageSize);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        Patch(strlSize, stream.Position - strlSize - 4);
        Patch(hdrlSize, stream.Position - hdrlSize - 4);
        FourCC("LIST"); var moviSize = stream.Position; writer.Write(0); FourCC("movi");
        var frameBytes = new byte[imageSize];
        for (var frame = 0; frame < frames; frame++)
        {
            var phase = (frame / 10) % 3;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 3;
                var marker = x >= 10 + (frame % 100) && x < 35 + (frame % 100) && y >= 25 && y < 55;
                frameBytes[offset] = marker ? (byte)255 : phase == 0 ? (byte)0 : phase == 1 ? (byte)0 : (byte)220;
                frameBytes[offset + 1] = marker ? (byte)255 : phase == 0 ? (byte)0 : phase == 1 ? (byte)220 : (byte)0;
                frameBytes[offset + 2] = marker ? (byte)255 : phase == 0 ? (byte)220 : phase == 1 ? (byte)0 : (byte)0;
            }
            FourCC("00db"); writer.Write(imageSize); writer.Write(frameBytes);
        }
        Patch(moviSize, stream.Position - moviSize - 4);
        Patch(riffSize, stream.Position - riffSize - 4);
    }
}
