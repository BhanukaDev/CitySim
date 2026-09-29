# Bakes the waterfall mist atlas (tools/fetch_particles.sh): args = source dir, then four puff names.
extends SceneTree

func _init() -> void:
	var args := OS.get_cmdline_user_args()
	var atlas := Image.create(512, 512, false, Image.FORMAT_L8)
	for i in 4:
		var img := Image.load_from_file("%s/%s.png" % [args[0], args[i + 1]])
		img.convert(Image.FORMAT_L8)
		img.resize(256, 256, Image.INTERPOLATE_LANCZOS)
		atlas.blit_rect(img, Rect2i(0, 0, 256, 256), Vector2i(i % 2 * 256, i / 2 * 256))
	var err := atlas.save_png("res://assets/particles/mist_puffs.png")
	print("bake_mist: assets/particles/mist_puffs.png (%s)" % error_string(err))
	quit()
