# Jellyfin 12.0 and 12.1+

Gelato builds against Jellyfin 12.0.0 and loads on 12.0.0 through 12.4.0.

## What changed in 12.1

Between 12.0 and 12.4 only one interface Gelato implements changed:
`IItemRepository` gained `GetTagNames(InternalItemsQuery)` (12.1). The
other decorated interfaces (`IMediaSourceManager`, `IProviderManager`,
`IDtoService`, `ICollectionManager`, `IPlaylistManager`,
`ISubtitleManager`, `ITrickplayManager`, `IImageProcessor`) are
unchanged. Members Gelato calls that changed shape (the rating and
loudness properties of `BaseItem` became full properties) kept their
getters and setters, so they stay binary compatible; the removed
`InternalItemsQuery.UseRawName` is not used.

## How one build serves both

`GelatoItemRepository.GetTagNames` is a public virtual method. When a
type loads, the runtime maps an interface method onto a public virtual
method with the same name and signature, so on 12.1 and later it
implements the new member, and on 12.0 it is an extra method nobody
calls. It has to be virtual (and the class unsealed): compiled against
12.0 the compiler does not know it implements anything and would emit a
plain method, which the 12.4 loader rejected. The call into Jellyfin's
own repository cannot be compiled against 12.0, so it is bound once by
reflection when the running Jellyfin has the method. Gelato's internal
tags (`gelato-stream`, `gelato-tree-synced`) are left out of the result,
so they do not appear in the tag filter.

One build was chosen over two targets: it is one method, and two
targets would mean two packages and a manifest entry per Jellyfin
version for no other difference.

## Proof

The same steps on both images with no published ports, from a copy of a
12.0 bench database (426k items; 12.4 migrated it on first start, as an
upgrade of the live server would):

    image: Linuxserver.io version:- 12.0ubu2604-ls48
    server: 12.0.0
    [INF] Emby.Server.Implementations.Plugins.PluginManager: Loaded plugin: Gelato 0.26.0.0
    search Heat: 200 1.11 s results=58
    open result: 200 3.36 s canonical_id_changed=yes sources=15
    playbackinfo: 200 0.93 s container=mkv video=True
    stream: 206 1048576 bytes
    filters2 (tags): 200 tags=[]
    errors: 0

    image: Linuxserver.io version:- 12.4ubu2604-ls58
    server: 12.4.0
    [INF] Emby.Server.Implementations.Plugins.PluginManager: Loaded plugin: Gelato 0.26.0.0
    search Heat: 200 1.07 s results=58
    open result: 200 2.63 s canonical_id_changed=yes sources=15
    playbackinfo: 200 0.64 s container=mkv video=True
    stream: 206 1048576 bytes
    filters2 (tags): 200 tags=[]
    errors: 0

v0.26.25.0 on the same 12.4 image, for comparison:

    [ERR] Emby.Server.Implementations.Plugins.PluginManager: Failed to load assembly /config/data/plugins/Gelato/Gelato.dll ... Disabling plugin
    Method 'GetTagNames' in type 'Gelato.Decorators.GelatoItemRepository' ... does not have an implementation.

(The first search on a freshly started server takes about 1.1 s; once
warm, the same search takes 0.5 s, see SEARCH_PERFORMANCE.md.)
