namespace MineGenerator.Interfaces
{
    public interface IMineChunksCollectable
    {
        void AddMineChunks(params MineBezierChunk[] mineChunks);
    }
}